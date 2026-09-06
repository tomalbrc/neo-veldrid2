using System.Collections.Generic;
using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;

namespace NeoVeldrid.Vk;

internal unsafe class VkResourceSet : ResourceSet
{
    private readonly VkGraphicsDevice _gd;
    private readonly DescriptorResourceCounts _descriptorCounts;
    private readonly DescriptorAllocationToken _descriptorAllocationToken;
    private readonly List<ResourceRefCount> _refCounts = new List<ResourceRefCount>();
    private bool _destroyed;
    private string _name;

    public DescriptorSet DescriptorSet => _descriptorAllocationToken.Set;

    private readonly List<VkTexture> _sampledTextures = new List<VkTexture>();
    public List<VkTexture> SampledTextures => _sampledTextures;
    private readonly List<VkTexture> _storageImages = new List<VkTexture>();
    public List<VkTexture> StorageTextures => _storageImages;

    public ResourceRefCount RefCount { get; }
    public List<ResourceRefCount> RefCounts => _refCounts;

    public override bool IsDisposed => _destroyed;

    public VkResourceSet(VkGraphicsDevice gd, ref ResourceSetDescription description)
        : base(ref description)
    {
        _gd = gd;
        RefCount = new ResourceRefCount(DisposeCore);
        VkResourceLayout vkLayout = Util.AssertSubtype<ResourceLayout, VkResourceLayout>(description.Layout);

        BindableResource[] boundResources = description.BoundResources;

        // The number of actual layout bindings (e.g., 2 slots)
        int totalBindings = vkLayout.DescriptorTypes.Length;

        uint[] actualCounts = ComputeActualCounts(vkLayout, boundResources);

        List<uint> variableCounts = new List<uint>();
        for (int i = 0; i < totalBindings; i++)
        {
            if ((vkLayout.GetElementOptions(i) & ResourceLayoutElementOptions.VariableDescriptorCount) != 0)
                variableCounts.Add(actualCounts[i]);
        }

        DescriptorSetLayout dsl = vkLayout.DescriptorSetLayout;
        _descriptorCounts = vkLayout.DescriptorResourceCounts;
        _descriptorAllocationToken = _gd.DescriptorPoolManager.Allocate(
            _descriptorCounts, dsl, variableCounts.ToArray());

        uint totalActualDescriptors = 0;
        for (int i = 0; i < totalBindings; i++)
            totalActualDescriptors += actualCounts[i];

        WriteDescriptorSet* descriptorWrites = stackalloc WriteDescriptorSet[totalBindings];
        DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[(int)totalActualDescriptors];
        DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[(int)totalActualDescriptors];

        int resourceIndex = 0;
        int bufferInfoIndex = 0;
        int imageInfoIndex = 0;

        for (int i = 0; i < totalBindings; i++)
        {
            DescriptorType type = vkLayout.DescriptorTypes[i];
            uint count = actualCounts[i]; // actual count, not max

            descriptorWrites[i].SType = StructureType.WriteDescriptorSet;
            descriptorWrites[i].DescriptorCount = count;
            descriptorWrites[i].DescriptorType = type;
            descriptorWrites[i].DstBinding = (uint)i;
            descriptorWrites[i].DstSet = _descriptorAllocationToken.Set;

            if (count == 1)
            {
                BindableResource resource = boundResources[resourceIndex++];

                if (IsBufferType(type))
                {
                    DeviceBufferRange range = Util.GetBufferRange(resource, 0);
                    VkBuffer rangedVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(range.Buffer);
                    bufferInfos[bufferInfoIndex].Buffer = rangedVkBuffer.DeviceBuffer;
                    bufferInfos[bufferInfoIndex].Offset = range.Offset;
                    bufferInfos[bufferInfoIndex].Range = range.SizeInBytes;
                    descriptorWrites[i].PBufferInfo = &bufferInfos[bufferInfoIndex];
                    bufferInfoIndex++;
                    _refCounts.Add(rangedVkBuffer.RefCount);
                }
                else if (type == DescriptorType.SampledImage)
                {
                    TextureView texView = Util.GetTextureView(_gd, resource);
                    VkTextureView vkTexView = Util.AssertSubtype<TextureView, VkTextureView>(texView);
                    imageInfos[imageInfoIndex].ImageView = vkTexView.ImageView;
                    imageInfos[imageInfoIndex].ImageLayout = ImageLayout.ShaderReadOnlyOptimal;
                    descriptorWrites[i].PImageInfo = &imageInfos[imageInfoIndex];
                    imageInfoIndex++;
                    _sampledTextures.Add(Util.AssertSubtype<Texture, VkTexture>(texView.Target));
                    _refCounts.Add(vkTexView.RefCount);
                }
                else if (type == DescriptorType.StorageImage)
                {
                    TextureView texView = Util.GetTextureView(_gd, resource);
                    VkTextureView vkTexView = Util.AssertSubtype<TextureView, VkTextureView>(texView);
                    imageInfos[imageInfoIndex].ImageView = vkTexView.ImageView;
                    imageInfos[imageInfoIndex].ImageLayout = ImageLayout.General;
                    descriptorWrites[i].PImageInfo = &imageInfos[imageInfoIndex];
                    imageInfoIndex++;
                    _storageImages.Add(Util.AssertSubtype<Texture, VkTexture>(texView.Target));
                    _refCounts.Add(vkTexView.RefCount);
                }
                else if (type == DescriptorType.Sampler)
                {
                    VkSampler sampler = Util.AssertSubtype<BindableResource, VkSampler>(resource);
                    imageInfos[imageInfoIndex].Sampler = sampler.DeviceSampler;
                    descriptorWrites[i].PImageInfo = &imageInfos[imageInfoIndex];
                    imageInfoIndex++;
                    _refCounts.Add(sampler.RefCount);
                }
            }
            else // array binding (count > 1)
            {
                if (IsBufferType(type))
                {
                    int baseInfoIdx = bufferInfoIndex;
                    for (int j = 0; j < count; j++)
                    {
                        BindableResource resource = boundResources[resourceIndex++];
                        DeviceBufferRange range = Util.GetBufferRange(resource, 0);
                        VkBuffer rangedVkBuffer = Util.AssertSubtype<DeviceBuffer, VkBuffer>(range.Buffer);
                        bufferInfos[baseInfoIdx + j].Buffer = rangedVkBuffer.DeviceBuffer;
                        bufferInfos[baseInfoIdx + j].Offset = range.Offset;
                        bufferInfos[baseInfoIdx + j].Range = range.SizeInBytes;
                        _refCounts.Add(rangedVkBuffer.RefCount);
                        bufferInfoIndex++;
                    }
                    descriptorWrites[i].PBufferInfo = &bufferInfos[baseInfoIdx];
                }
                else if (type == DescriptorType.SampledImage)
                {
                    int baseInfoIdx = imageInfoIndex;
                    for (int j = 0; j < count; j++)
                    {
                        BindableResource resource = boundResources[resourceIndex++];
                        TextureView texView = Util.GetTextureView(_gd, resource);
                        VkTextureView vkTexView = Util.AssertSubtype<TextureView, VkTextureView>(texView);
                        imageInfos[baseInfoIdx + j].ImageView = vkTexView.ImageView;
                        imageInfos[baseInfoIdx + j].ImageLayout = ImageLayout.ShaderReadOnlyOptimal;
                        _sampledTextures.Add(Util.AssertSubtype<Texture, VkTexture>(texView.Target));
                        _refCounts.Add(vkTexView.RefCount);
                        imageInfoIndex++;
                    }
                    descriptorWrites[i].PImageInfo = &imageInfos[baseInfoIdx];
                }
                // StorageImage and Sampler arrays are not shown but would follow similar pattern
            }
        }

        _gd.Vk.UpdateDescriptorSets(_gd.Device, (uint)totalBindings, descriptorWrites, 0, null);
    }

    // Helper to compute actual counts (flattened resources are in binding order)
    private uint[] ComputeActualCounts(VkResourceLayout layout, BindableResource[] resources)
    {
        uint[] counts = new uint[layout.DescriptorTypes.Length];
        int idx = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            uint max = layout.DescriptorCounts[i];
            bool isVariable = (layout.GetElementOptions(i) & ResourceLayoutElementOptions.VariableDescriptorCount) != 0;

            if (isVariable)
            {
                // We take whatever resources remain up to max
                uint actual = 0;
                while (idx + actual < resources.Length && actual < max)
                {
                    actual++;
                }
                counts[i] = actual;
                idx += (int)actual;
            }
            else
            {
                // Must be exactly max
                counts[i] = max;
                idx += (int)max;
            }
        }
        // If you want to validate that idx == resources.Length, you could assert.
        return counts;
    }

    private bool IsBufferType(DescriptorType type) =>
        type == DescriptorType.UniformBuffer ||
        type == DescriptorType.UniformBufferDynamic ||
        type == DescriptorType.StorageBuffer ||
        type == DescriptorType.StorageBufferDynamic;

    public override string Name { get => _name; set { _name = value; _gd.SetResourceName(this, value); } }

    public override void Dispose() => RefCount.Decrement();

    private void DisposeCore()
    {
        if (!_destroyed)
        {
            _destroyed = true;
            _gd.DescriptorPoolManager.Free(_descriptorAllocationToken, _descriptorCounts);
        }
    }
}