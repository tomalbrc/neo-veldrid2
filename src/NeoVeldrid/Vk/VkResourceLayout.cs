using Silk.NET.Vulkan;
using static NeoVeldrid.Vk.VulkanUtil;

namespace NeoVeldrid.Vk;

internal unsafe class VkResourceLayout : ResourceLayout
{
    private readonly VkGraphicsDevice _gd;
    private readonly DescriptorSetLayout _dsl;
    private readonly DescriptorType[] _descriptorTypes;
    private readonly uint[] _descriptorCounts;
    private readonly ResourceLayoutElementOptions[] _elementOptions; // NEW

    private bool _disposed;
    private string _name;

    public DescriptorSetLayout DescriptorSetLayout => _dsl;
    public DescriptorType[] DescriptorTypes => _descriptorTypes;
    public DescriptorResourceCounts DescriptorResourceCounts { get; }
    public new int DynamicBufferCount { get; }
    public uint[] DescriptorCounts => _descriptorCounts;

    public override bool IsDisposed => _disposed;

    public VkResourceLayout(VkGraphicsDevice gd, ref ResourceLayoutDescription description)
        : base(ref description)
    {
        _gd = gd;
        ResourceLayoutElementDescription[] elements = description.Elements;
        _descriptorTypes = new DescriptorType[elements.Length];
        _descriptorCounts = new uint[elements.Length];
        _elementOptions = new ResourceLayoutElementOptions[elements.Length]; // store options

        DescriptorSetLayoutCreateInfo dslCI = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo
        };

        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[elements.Length];

        uint uniformBufferCount = 0;
        uint uniformBufferDynamicCount = 0;
        uint sampledImageCount = 0;
        uint samplerCount = 0;
        uint storageBufferCount = 0;
        uint storageBufferDynamicCount = 0;
        uint storageImageCount = 0;

        for (uint i = 0; i < elements.Length; i++)
        {
            var el = elements[i];
            _elementOptions[i] = el.Options;
            _descriptorCounts[i] = el.DescriptorCount;

            DescriptorType descriptorType = VkFormats.VdToVkDescriptorType(el.Kind, el.Options);
            _descriptorTypes[i] = descriptorType;

            bindings[i].Binding = i;
            bindings[i].DescriptorCount = el.DescriptorCount; // set the max count
            bindings[i].DescriptorType = descriptorType;
            bindings[i].StageFlags = VkFormats.VdToVkShaderStages(el.Stages);

            if ((el.Options & ResourceLayoutElementOptions.DynamicBinding) != 0)
                DynamicBufferCount += 1;

            switch (descriptorType)
            {
                case DescriptorType.Sampler: samplerCount++; break;
                case DescriptorType.SampledImage: sampledImageCount++; break;
                case DescriptorType.StorageImage: storageImageCount++; break;
                case DescriptorType.UniformBuffer: uniformBufferCount++; break;
                case DescriptorType.UniformBufferDynamic: uniformBufferDynamicCount++; break;
                case DescriptorType.StorageBuffer: storageBufferCount++; break;
                case DescriptorType.StorageBufferDynamic: storageBufferDynamicCount++; break;
            }
        }

        DescriptorResourceCounts = new DescriptorResourceCounts(
            uniformBufferCount,
            uniformBufferDynamicCount,
            sampledImageCount,
            samplerCount,
            storageBufferCount,
            storageBufferDynamicCount,
            storageImageCount);

        DescriptorBindingFlags* bindingFlags = stackalloc DescriptorBindingFlags[elements.Length];
        for (uint i = 0; i < elements.Length; i++)
        {
            var flags = DescriptorBindingFlags.PartiallyBoundBit; // always safe
            if ((_elementOptions[i] & ResourceLayoutElementOptions.VariableDescriptorCount) != 0)
            {
                flags |= DescriptorBindingFlags.VariableDescriptorCountBit;
            }
            bindingFlags[i] = flags;
        }

        DescriptorSetLayoutBindingFlagsCreateInfo layoutFlagsCI = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = (uint)elements.Length,
            PBindingFlags = bindingFlags
        };

        dslCI.BindingCount = (uint)elements.Length;
        dslCI.PBindings = bindings;
        dslCI.PNext = &layoutFlagsCI;
        dslCI.Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit;

        Result result = _gd.Vk.CreateDescriptorSetLayout(_gd.Device, in dslCI, null, out _dsl);
        CheckResult(result);
    }

    // Helper to retrieve options for a binding (needed by VkResourceSet)
    public ResourceLayoutElementOptions GetElementOptions(int index) => _elementOptions[index];

    public override string Name { get => _name; set { _name = value; _gd.SetResourceName(this, value); } }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gd.Vk.DestroyDescriptorSetLayout(_gd.Device, _dsl, null);
        }
    }
}