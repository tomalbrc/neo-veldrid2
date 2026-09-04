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

        // Total flattened resources (e.g., 1 instance buffer + 4 chunk buffers = 5 descriptors)
        uint totalDescriptorCount = 0;
        for (int i = 0; i < totalBindings; i++)
        {
            totalDescriptorCount += vkLayout.DescriptorCounts[i];
        }

        DescriptorSetLayout dsl = vkLayout.DescriptorSetLayout;
        _descriptorCounts = vkLayout.DescriptorResourceCounts;
        _descriptorAllocationToken = _gd.DescriptorPoolManager.Allocate(_descriptorCounts, dsl, totalDescriptorCount);

        WriteDescriptorSet* descriptorWrites = stackalloc WriteDescriptorSet[totalBindings];
        DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[(int)totalDescriptorCount];
        DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[(int)totalDescriptorCount];

        int resourceIndex = 0;
        int bufferInfoIndex = 0;
        int imageInfoIndex = 0;

        for (int i = 0; i < totalBindings; i++)
        {
            DescriptorType type = vkLayout.DescriptorTypes[i];
            uint descriptorCount = vkLayout.DescriptorCounts[i];

            descriptorWrites[i].SType = StructureType.WriteDescriptorSet;
            descriptorWrites[i].DescriptorCount = descriptorCount;
            descriptorWrites[i].DescriptorType = type;
            descriptorWrites[i].DstBinding = (uint)i;
            descriptorWrites[i].DstSet = _descriptorAllocationToken.Set;

            if (descriptorCount == 1)
            {
                BindableResource resource = boundResources[resourceIndex++];

                if (type == DescriptorType.UniformBuffer || type == DescriptorType.UniformBufferDynamic
                    || type == DescriptorType.StorageBuffer || type == DescriptorType.StorageBufferDynamic)
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
            else
            {
                // Descriptor Indexing Array Mapping
                if (type == DescriptorType.UniformBuffer || type == DescriptorType.UniformBufferDynamic
                    || type == DescriptorType.StorageBuffer || type == DescriptorType.StorageBufferDynamic)
                {
                    int baseInfoIdx = bufferInfoIndex;
                    for (int j = 0; j < descriptorCount; j++)
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
                    for (int j = 0; j < descriptorCount; j++)
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
            }
        }

        _gd.Vk.UpdateDescriptorSets(_gd.Device, (uint)totalBindings, descriptorWrites, 0, null);
    }

    public override string Name
    {
        get => _name;
        set
        {
            _name = value;
            _gd.SetResourceName(this, value);
        }
    }

    public override void Dispose()
    {
        RefCount.Decrement();
    }

    private void DisposeCore()
    {
        if (!_destroyed)
        {
            _destroyed = true;
            _gd.DescriptorPoolManager.Free(_descriptorAllocationToken, _descriptorCounts);
        }
    }
}