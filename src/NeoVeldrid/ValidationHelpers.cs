using System.Diagnostics;

namespace NeoVeldrid;

internal static class ValidationHelpers
{
    [Conditional("VALIDATE_USAGE")]
    internal static void ValidateResourceSet(GraphicsDevice gd, ref ResourceSetDescription description)
    {
#if VALIDATE_USAGE
        ResourceLayoutElementDescription[] elements = description.Layout.Description.Elements;
        BindableResource[] resources = description.BoundResources;

        if (resources == null)
            throw new NeoVeldridException("BoundResources cannot be null.");

        uint totalMax = 0;
        uint[] maxCounts = new uint[elements.Length];
        bool[] isVariable = new bool[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            uint count = elements[i].DescriptorCount > 0 ? elements[i].DescriptorCount : 1;
            maxCounts[i] = count;
            totalMax += count;
            isVariable[i] = (elements[i].Options & ResourceLayoutElementOptions.VariableDescriptorCount) != 0;
        }

        if (resources.Length > totalMax)
            throw new NeoVeldridException(
                $"The number of resources specified ({resources.Length}) exceeds the total maximum descriptor count of the layout ({totalMax}).");

        int resourceIndex = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            uint expectedMax = maxCounts[i];
            uint actualCount;
            if (isVariable[i])
            {
                actualCount = 0;
                while (resourceIndex + actualCount < resources.Length && actualCount < expectedMax)
                    actualCount++;
            }
            else
            {
                if (resourceIndex + expectedMax > resources.Length)
                    throw new NeoVeldridException(
                        $"Not enough resources for binding {i}. Expected {expectedMax}, but only {resources.Length - resourceIndex} remaining.");
                actualCount = expectedMax;
            }

            for (uint j = 0; j < actualCount; j++)
            {
                BindableResource resource = resources[resourceIndex + j];
                ValidateResourceKind(elements[i].Kind, resource, (uint)i, j);

                if (elements[i].Kind == ResourceKind.UniformBuffer
                    || elements[i].Kind == ResourceKind.StructuredBufferReadOnly
                    || elements[i].Kind == ResourceKind.StructuredBufferReadWrite)
                {
                    DeviceBufferRange range = Util.GetBufferRange(resource, 0);
                    if (!gd.Features.BufferRangeBinding && (range.Offset != 0 || range.SizeInBytes != range.Buffer.SizeInBytes))
                    {
                        throw new NeoVeldridException($"The {nameof(DeviceBufferRange)} in slot {i}, index {j} uses a non-zero offset or less-than-full size, " +
                            $"which requires {nameof(GraphicsDeviceFeatures)}.{nameof(GraphicsDeviceFeatures.BufferRangeBinding)}.");
                    }

                    uint alignment = elements[i].Kind == ResourceKind.UniformBuffer
                       ? gd.UniformBufferMinOffsetAlignment
                       : gd.StructuredBufferMinOffsetAlignment;

                    if ((range.Offset % alignment) != 0)
                    {
                        throw new NeoVeldridException($"The {nameof(DeviceBufferRange)} in slot {i}, index {j} has an invalid offset: {range.Offset}. " +
                            $"The offset for this buffer must be a multiple of {alignment}.");
                    }
                }
            }

            resourceIndex += (int)actualCount;
        }

        if (resourceIndex < resources.Length)
            throw new NeoVeldridException(
                $"Too many resources specified; expected at most {totalMax}, got {resources.Length}.");
#endif
    }

    [Conditional("VALIDATE_USAGE")]
    private static void ValidateResourceKind(ResourceKind kind, BindableResource resource, uint slot, uint index)
    {
        switch (kind)
        {
            case ResourceKind.UniformBuffer:
                {
                    if (!Util.GetDeviceBuffer(resource, out DeviceBuffer b)
                        || (b.Usage & BufferUsage.UniformBuffer) == 0)
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)}.{kind} specified in the {nameof(ResourceLayout)}. " +
                            $"It must be a {nameof(DeviceBuffer)} or {nameof(DeviceBufferRange)} with {nameof(BufferUsage)}.{nameof(BufferUsage.UniformBuffer)}.");
                    }
                    break;
                }
            case ResourceKind.StructuredBufferReadOnly:
                {
                    if (!Util.GetDeviceBuffer(resource, out DeviceBuffer b)
                        || (b.Usage & (BufferUsage.StructuredBufferReadOnly | BufferUsage.StructuredBufferReadWrite)) == 0)
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)}.{kind} specified in the {nameof(ResourceLayout)}. It must be a {nameof(DeviceBuffer)} with {nameof(BufferUsage)}.{nameof(BufferUsage.StructuredBufferReadOnly)}.");
                    }
                    break;
                }
            case ResourceKind.StructuredBufferReadWrite:
                {
                    if (!Util.GetDeviceBuffer(resource, out DeviceBuffer b)
                        || (b.Usage & BufferUsage.StructuredBufferReadWrite) == 0)
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)} specified in the {nameof(ResourceLayout)}. It must be a {nameof(DeviceBuffer)} with {nameof(BufferUsage)}.{nameof(BufferUsage.StructuredBufferReadWrite)}.");
                    }
                    break;
                }
            case ResourceKind.TextureReadOnly:
                {
                    if (!(resource is TextureView tv && (tv.Target.Usage & TextureUsage.Sampled) != 0)
                        && !(resource is Texture t && (t.Usage & TextureUsage.Sampled) != 0))
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)}.{kind} specified in the " +
                            $"{nameof(ResourceLayout)}. It must be a {nameof(Texture)} or {nameof(TextureView)} whose target " +
                            $"has {nameof(TextureUsage)}.{nameof(TextureUsage.Sampled)}.");
                    }
                    break;
                }
            case ResourceKind.TextureReadWrite:
                {
                    if (!(resource is TextureView tv && (tv.Target.Usage & TextureUsage.Storage) != 0)
                        && !(resource is Texture t && (t.Usage & TextureUsage.Storage) != 0))
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)}.{kind} specified in the " +
                            $"{nameof(ResourceLayout)}. It must be a {nameof(Texture)} or {nameof(TextureView)} whose target " +
                            $"has {nameof(TextureUsage)}.{nameof(TextureUsage.Storage)}.");
                    }
                    break;
                }
            case ResourceKind.Sampler:
                {
                    if (resource is not Sampler s)
                    {
                        throw new NeoVeldridException(
                            $"Resource in slot {slot}, index {index} does not match {nameof(ResourceKind)}.{kind} specified in the {nameof(ResourceLayout)}. It must be a {nameof(Sampler)}.");
                    }
                    break;
                }
            default:
                throw Illegal.Value<ResourceKind>();
        }
    }
}