using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ObjLoader.Loader.Data.Elements;
using ObjLoader.Loader.Data.VertexData;
using ObjLoader.Loader.Loaders;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Veldrid;
using WolvField.Properties;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace WolvField;

using Vec4 = Vector4D<float>;

public static unsafe class Program
{
    private record VkCtx
    {
        public Vk? vk;

        public Instance instance;

        public ExtDebugUtils? debugUtils;
        public DebugUtilsMessengerEXT debugMessenger;

        public PhysicalDevice physicalDevice;
        public uint computeIndex;
        public Device device;

        public CommandPool compCmdPool;
        public CommandBuffer compCmdBuf;
        public Pipeline compPipeline;
        public PipelineLayout compPipelineLayout;
        public DescriptorSet compDescSet;
        public DescriptorSetLayout compDescSetLayout;
        public Buffer compBufIn;
        public Buffer compBufOut;
        public DeviceMemory compBufMemIn;
        public DeviceMemory compBufMemOut;

        public DescriptorPool descPool;
        public Queue queue;
    }

    private static readonly string[] ValidationLayers =
    {
        "VK_LAYER_KHRONOS_validation"
    };

    private static bool _hasValidationLayerSupport;

    private static bool CheckValidationLayerSupport(ref VkCtx ctx)
    {
        uint layerCount = 0;
        ctx.vk!.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
            ctx.vk!.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);

        HashSet<string?> availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName)).ToHashSet();

        return ValidationLayers.All(availableLayerNames.Contains);
    }

    private static uint DebugCallback
    (
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityVerboseBitExt))
            return Vk.False;
        var message = $"Validation layer: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}";
        if (messageSeverity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityErrorBitExt))
            Console.Error.WriteLine(message);
        else
            Console.Out.WriteLine(message);
        return Vk.False;
    }

    private static void CreateInstance(VkCtx ctx)
    {
        ctx.vk = Vk.GetApi();

        _hasValidationLayerSupport = CheckValidationLayerSupport(ref ctx);

        ApplicationInfo appInfo = new(pApplicationName: (byte*)Marshal.StringToHGlobalAnsi("WSC"),
                                      applicationVersion: new Version32(1, 0, 0),
                                      pEngineName: (byte*)Marshal.StringToHGlobalAnsi("WSC Super Engine"),
                                      engineVersion: new Version32(1, 0, 0),
                                      apiVersion: Vk.Version12);

        InstanceCreateInfo createInfo = new(pApplicationInfo: &appInfo, enabledExtensionCount: 0);

        if (_hasValidationLayerSupport)
        {
            string[] extensions = { ExtDebugUtils.ExtensionName };
            createInfo.EnabledExtensionCount = (uint)extensions.Length;
            createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

            createInfo.EnabledLayerCount = (uint)ValidationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(ValidationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new(messageSeverity: DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityVerboseBitExt |
                                                                                    DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityWarningBitExt |
                                                                                    DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityErrorBitExt,
                                                                   messageType: DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypeGeneralBitExt |
                                                                                DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypePerformanceBitExt |
                                                                                DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypeValidationBitExt,
                                                                   pfnUserCallback: (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback);
            createInfo.PNext = &debugCreateInfo;
        }

        Check(ctx.vk!.CreateInstance(createInfo, null, out ctx.instance), "Failed to create instance!");

        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (_hasValidationLayerSupport)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
    }

    private static uint? FindComputeIndex(VkCtx ctx, PhysicalDevice device)
    {
        uint? computeIndex = null;

        uint queueFamilyCount = 0;
        ctx.vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            ctx.vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);

        for (var familyIdx = 0; familyIdx < queueFamilies.Length; familyIdx++)
        {
            QueueFamilyProperties queueFamily = queueFamilies[familyIdx];
            if (!queueFamily.QueueFlags.HasFlag(QueueFlags.QueueComputeBit)) continue;

            computeIndex = (uint)familyIdx;
            break;
        }

        return computeIndex;
    }

    private static void PickPhysicalDevice(VkCtx ctx)
    {
        uint deviceCount = 0;
        ctx.vk!.EnumeratePhysicalDevices(ctx.instance, ref deviceCount, null);

        if (deviceCount == 0)
            throw new Exception("Failed to find a graphics device!");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
            ctx.vk!.EnumeratePhysicalDevices(ctx.instance, ref deviceCount, devicesPtr);

        foreach (PhysicalDevice dev in devices)
        {
            if (FindComputeIndex(ctx, dev) is not { } computeIndex) continue;

            ctx.physicalDevice = dev;
            ctx.computeIndex = computeIndex;
            break;
        }

        if (ctx.physicalDevice.Handle == default)
            throw new Exception("Failed to find a suitable GPU!");
    }

    private static void CreateLogicalDevice(VkCtx ctx)
    {
        var queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo(queueFamilyIndex: ctx.computeIndex,
                                                        queueCount: 1,
                                                        pQueuePriorities: &queuePriority);
        PhysicalDeviceFeatures deviceFeatures = new();
        DeviceCreateInfo createInfo = new(queueCreateInfoCount: 1,
                                          pQueueCreateInfos: &queueCreateInfo,
                                          pEnabledFeatures: &deviceFeatures,
                                          enabledExtensionCount: 0,
                                          enabledLayerCount: 0);

        if (_hasValidationLayerSupport)
        {
            createInfo.EnabledLayerCount = (uint)ValidationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(ValidationLayers);
        }

        Check(ctx.vk!.CreateDevice(ctx.physicalDevice, in createInfo, null, out ctx.device), "Failed to create logical device!");

        ctx.vk!.GetDeviceQueue(ctx.device, ctx.computeIndex, 0, out ctx.queue);

        if (_hasValidationLayerSupport)
        {
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        }
    }

    private static void CreateCompBuffers(VkCtx ctx, IList<Vertex> vertices)
    {
        var outBufSize = (ulong)(Unsafe.SizeOf<Vec4>() * 1588);
        CreateBuffer(ctx, outBufSize,
                     BufferUsageFlags.BufferUsageTransferDstBit | BufferUsageFlags.BufferUsageStorageBufferBit,
                     MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyDeviceLocalBit,
                     ref ctx.compBufOut, ref ctx.compBufMemOut);

        var inBufSize = (ulong)(Unsafe.SizeOf<Vec4>() * 1588);
        Buffer stagingBuf = default;
        DeviceMemory stagingBufMem = default;
        CreateBuffer(ctx, inBufSize,
                     BufferUsageFlags.BufferUsageTransferSrcBit,
                     MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyHostCoherentBit,
                     ref stagingBuf, ref stagingBufMem);
        void* data;
        ctx.vk!.MapMemory(ctx.device, stagingBufMem, 0, inBufSize, 0, &data);
        var points = new Vec4[vertices.Count];
        for (var i = 0; i < 1588; i++)
        {
            Vertex v = vertices[i];
            points[i] = new Vec4(v.X, v.Y, v.Z, 0.0f);
        }
        points.AsSpan().CopyTo(new Span<Vec4>(data, points.Length));
        ctx.vk!.UnmapMemory(ctx.device, stagingBufMem);
        CreateBuffer(ctx, inBufSize,
                     BufferUsageFlags.BufferUsageTransferDstBit | BufferUsageFlags.BufferUsageStorageBufferBit,
                     MemoryPropertyFlags.MemoryPropertyDeviceLocalBit,
                     ref ctx.compBufIn, ref ctx.compBufMemIn);
        CopyBuffer(ctx, stagingBuf, ctx.compBufIn, inBufSize);
        ctx.vk!.DestroyBuffer(ctx.device, stagingBuf, null);
        ctx.vk!.FreeMemory(ctx.device, stagingBufMem, null);
    }

    private static void SetupDebugMessenger(VkCtx ctx)
    {
        if (!_hasValidationLayerSupport || !ctx.vk!.TryGetInstanceExtension(ctx.instance, out ctx.debugUtils)) return;

        DebugUtilsMessengerCreateInfoEXT createInfo = new(sType: StructureType.DebugUtilsMessengerCreateInfoExt,
                                                          messageSeverity: DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityVerboseBitExt |
                                                                           DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityWarningBitExt |
                                                                           DebugUtilsMessageSeverityFlagsEXT.DebugUtilsMessageSeverityErrorBitExt,
                                                          messageType: DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypeGeneralBitExt |
                                                                       DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypePerformanceBitExt |
                                                                       DebugUtilsMessageTypeFlagsEXT.DebugUtilsMessageTypeValidationBitExt,
                                                          pfnUserCallback: (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback);

        Check(ctx.debugUtils!.CreateDebugUtilsMessenger(ctx.instance, in createInfo, null, out ctx.debugMessenger), "Failed to set up debug messenger!");
    }

    private static void CreateCompCmdBuffers(VkCtx ctx)
    {
        CommandPoolCreateInfo poolInfo = new(flags: CommandPoolCreateFlags.CommandPoolCreateResetCommandBufferBit,
                                             queueFamilyIndex: ctx.computeIndex);
        Check(ctx.vk!.CreateCommandPool(ctx.device, poolInfo, null, out ctx.compCmdPool),
              "Failed to create compute command pool!");
        CommandBufferAllocateInfo allocInfo = new(commandPool: ctx.compCmdPool, level: CommandBufferLevel.Primary, commandBufferCount: 1);
        fixed (CommandBuffer* cmdBufPtr = &ctx.compCmdBuf)
            Check(ctx.vk!.AllocateCommandBuffers(ctx.device, allocInfo, cmdBufPtr),
                  "Failed to allocate compute command buffers!");
    }

    private static void CreateDescriptorPool(VkCtx ctx)
    {
        DescriptorPoolSize storagePoolSize = new(type: DescriptorType.StorageBuffer, descriptorCount: 2);

        DescriptorPoolCreateInfo poolInfo = new(poolSizeCount: 1, pPoolSizes: &storagePoolSize, maxSets: 1);

        fixed (DescriptorPool* descriptorPoolPtr = &ctx.descPool)
            Check(ctx.vk!.CreateDescriptorPool(ctx.device, poolInfo, null, descriptorPoolPtr), "Failed to create descriptor pool!");
    }

    private static void CreateCompDescSet(VkCtx ctx)
    {
        DescriptorSetLayoutBinding* layoutBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ShaderStageComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ShaderStageComputeBit)
        };

        DescriptorSetLayoutCreateInfo layoutInfo = new(bindingCount: 2, pBindings: layoutBindings);

        fixed (DescriptorSet* descriptorSetsPtr = &ctx.compDescSet)
        fixed (DescriptorSetLayout* descSetLayoutPtr = &ctx.compDescSetLayout)
        {
            Check(ctx.vk!.CreateDescriptorSetLayout(ctx.device, layoutInfo, null, descSetLayoutPtr),
                  "Failed to create compute descriptor set layout!");

            DescriptorSetAllocateInfo allocInfo = new(descriptorPool: ctx.descPool, descriptorSetCount: 1, pSetLayouts: descSetLayoutPtr);
            Check(ctx.vk!.AllocateDescriptorSets(ctx.device, allocInfo, descriptorSetsPtr),
                  "Failed to allocate compute descriptor sets!");

            DescriptorBufferInfo inBufInfo = new(ctx.compBufIn, 0, (uint)Unsafe.SizeOf<Vec4>() * 1588);
            WriteDescriptorSet inWriteDescSet = new(dstSet: ctx.compDescSet,
                                                    dstBinding: 0,
                                                    descriptorType: DescriptorType.StorageBuffer,
                                                    descriptorCount: 1,
                                                    pBufferInfo: &inBufInfo);
            DescriptorBufferInfo outBufInfo = new(ctx.compBufOut, 0, (uint)Unsafe.SizeOf<Vec4>() * 1588);
            WriteDescriptorSet outWriteDescSet = new(dstSet: ctx.compDescSet,
                                                     dstBinding: 1,
                                                     descriptorType: DescriptorType.StorageBuffer,
                                                     descriptorCount: 1,
                                                     pBufferInfo: &outBufInfo);
            WriteDescriptorSet* writeDescSets = stackalloc WriteDescriptorSet[] { inWriteDescSet, outWriteDescSet };
            ctx.vk!.UpdateDescriptorSets(ctx.device, 2, writeDescSets, 0, null);
        }
    }

    private static void CreateComputePipeline(VkCtx ctx)
    {
        byte[] shaderCode = Resources.Compute;
        ShaderModule compShaderModule = CreateShaderModule(ctx, shaderCode);
        PipelineShaderStageCreateInfo pipelineStageCreateInfo = new(stage: ShaderStageFlags.ShaderStageComputeBit,
                                                                    module: compShaderModule,
                                                                    pName: (byte*)SilkMarshal.StringToPtr("main"));

        fixed (DescriptorSetLayout* descSetLayoutPtr = &ctx.compDescSetLayout)
        fixed (Pipeline* computePipeline = &ctx.compPipeline)
        {
            PipelineLayoutCreateInfo pipelineLayoutCreateInfo = new(setLayoutCount: 1, pSetLayouts: descSetLayoutPtr);
            Check(ctx.vk!.CreatePipelineLayout(ctx.device, pipelineLayoutCreateInfo, null, out ctx.compPipelineLayout),
                  "Failed to create compute pipeline layout!");
            ComputePipelineCreateInfo computePipelineCreateInfo = new(stage: pipelineStageCreateInfo, layout: ctx.compPipelineLayout);
            Check(ctx.vk!.CreateComputePipelines(ctx.device, default, 1, &computePipelineCreateInfo, null, computePipeline), "Failed to create compute pipeline!");
        }

        ctx.vk!.DestroyShaderModule(ctx.device, compShaderModule, null);
    }

    private static ShaderModule CreateShaderModule(VkCtx ctx, byte[] code)
    {
        ShaderModule shaderModule;
        fixed (byte* codePtr = code)
        {
            ShaderModuleCreateInfo createInfo = new(codeSize: (nuint)code.Length, pCode: (uint*)codePtr);
            Check(ctx.vk!.CreateShaderModule(ctx.device, createInfo, null, out shaderModule), "Failed to create shader module");
        }
        return shaderModule;
    }

    private static uint FindMemoryType(VkCtx ctx, uint typeFilter, MemoryPropertyFlags properties)
    {
        ctx.vk!.GetPhysicalDeviceMemoryProperties(ctx.physicalDevice, out PhysicalDeviceMemoryProperties memProperties);
        for (var i = 0; i < memProperties.MemoryTypeCount; i++)
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        throw new Exception("Failed to find suitable memory type!");
    }

    private static void CopyBuffer(VkCtx ctx, Buffer srcBuffer, Buffer dstBuffer, ulong size)
    {
        Vk vk = ctx.vk!;
        CommandBufferAllocateInfo allocateInfo = new(level: CommandBufferLevel.Primary, commandPool: ctx.compCmdPool, commandBufferCount: 1);
        vk.AllocateCommandBuffers(ctx.device, allocateInfo, out CommandBuffer commandBuffer);
        CommandBufferBeginInfo beginInfo = new(flags: CommandBufferUsageFlags.CommandBufferUsageOneTimeSubmitBit);
        vk.BeginCommandBuffer(commandBuffer, beginInfo);
        BufferCopy copyRegion = new(size: size);
        vk.CmdCopyBuffer(commandBuffer, srcBuffer, dstBuffer, 1, copyRegion);
        vk.EndCommandBuffer(commandBuffer);
        SubmitInfo submitInfo = new(commandBufferCount: 1, pCommandBuffers: &commandBuffer);
        vk.QueueSubmit(ctx.queue, 1, submitInfo, default);
        vk.QueueWaitIdle(ctx.queue);
        vk.FreeCommandBuffers(ctx.device, ctx.compCmdPool, 1, commandBuffer);
    }

    private static void CreateBuffer(VkCtx ctx, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, ref Buffer buffer, ref DeviceMemory bufferMemory)
    {
        BufferCreateInfo bufferInfo = new(size: size, usage: usage, sharingMode: SharingMode.Exclusive);
        fixed (Buffer* bufferPtr = &buffer)
            Check(ctx.vk!.CreateBuffer(ctx.device, bufferInfo, null, bufferPtr), "Failed to create vertex buffer!");

        ctx.vk!.GetBufferMemoryRequirements(ctx.device, buffer, out MemoryRequirements memRequirements);

        MemoryAllocateInfo allocateInfo = new(allocationSize: memRequirements.Size,
                                              memoryTypeIndex: FindMemoryType(ctx, memRequirements.MemoryTypeBits, properties));
        fixed (DeviceMemory* bufferMemoryPtr = &bufferMemory)
            Check(ctx.vk!.AllocateMemory(ctx.device, allocateInfo, null, bufferMemoryPtr), "Failed to allocate vertex buffer memory!");

        ctx.vk!.BindBufferMemory(ctx.device, buffer, bufferMemory, 0);
    }

    private static void Check(Result result, string message)
    {
        if (result != Result.Success)
            throw new Exception($"{message}: {result}");
    }

    private static List<uint> GetFlatIndices(this IEnumerable<Face> faces) =>
        faces.SelectMany(face =>
        {
            int count = face.Count;
            var faceIndices = new uint[count];
            for (var i = 0; i < count; i++)
                faceIndices[i] = (uint)face[i].VertexIndex;
            return faceIndices;
        }).ToList();

    private static void DecryptModel(VkCtx ctx, LoadResult obj)
    {
        Vk vk = ctx.vk!;
        fixed (CommandBuffer* cmdBufPtr = &ctx.compCmdBuf)
        fixed (DescriptorSet* descSetLayout = &ctx.compDescSet)
        {
            CommandBufferBeginInfo beginInfo = new(StructureType.CommandBufferBeginInfo);
            vk.BeginCommandBuffer(ctx.compCmdBuf, beginInfo);
            vk.CmdBindPipeline(ctx.compCmdBuf, PipelineBindPoint.Compute, ctx.compPipeline);
            vk.CmdBindDescriptorSets(ctx.compCmdBuf, PipelineBindPoint.Compute,
                                     ctx.compPipelineLayout, 0, 1, descSetLayout,
                                     0, null);
            vk.CmdDispatch(ctx.compCmdBuf, (uint)obj.Vertices.Count / 32 + 1, 1, 1);
            vk.EndCommandBuffer(ctx.compCmdBuf);
            SubmitInfo submitInfo = new(commandBufferCount: 1, pCommandBuffers: cmdBufPtr);
            vk.QueueSubmit(ctx.queue, 1, submitInfo, default);
            vk.QueueWaitIdle(ctx.queue);
        }

        List<uint> indices = obj.Groups.First().Faces.GetFlatIndices();
        indices.DeShuffle(1337);
    }

    private static int[] GetShuffleExchanges(int size, int key)
    {
        var exchanges = new int[size - 1];
        var rand = new Random(key);
        for (int i = size - 1; i > 0; i--)
        {
            int n = rand.Next(i + 1);
            exchanges[size - 1 - i] = n;
        }
        return exchanges;
    }

    private static void DeShuffle<T>(this IList<T> arr, int key)
    {
        int size = arr.Count;
        int[] exchanges = GetShuffleExchanges(size, key);
        for (var i = 1; i < size; i++)
        {
            int n = exchanges[size - i - 1];
            (arr[i], arr[n]) = (arr[n], arr[i]);
        }
    }

    private static void Main()
    {
        var objLoaderFactory = new ObjLoaderFactory();
        IObjLoader objLoader = objLoaderFactory.Create();
        LoadResult obj = objLoader.Load(new MemoryStream(Resources.Model));

        // var objLoaderFactory = new ObjLoaderFactory();
        // IObjLoader objLoader = objLoaderFactory.Create();
        // LoadResult obj = objLoader.Load(new FileStream("./Flag.obj", FileMode.Open));

        RenderDoc.Load(out RenderDoc? renderDoc);
        renderDoc?.StartFrameCapture();

        VkCtx ctx = new();
        CreateInstance(ctx);
        SetupDebugMessenger(ctx);
        PickPhysicalDevice(ctx);
        CreateLogicalDevice(ctx);
        CreateDescriptorPool(ctx);
        CreateCompCmdBuffers(ctx);
        CreateCompBuffers(ctx, obj.Vertices);
        CreateCompDescSet(ctx);
        CreateComputePipeline(ctx);

        // EncryptModel(ctx, obj);
        DecryptModel(ctx, obj);

        renderDoc?.EndFrameCapture();

        Console.WriteLine("Welcome to WolvField 4220!");
        Console.WriteLine("We hope you enjoy this rushed copy pasta game with ZERO CONTENT!");
        Console.WriteLine("Please preorder our upcoming $100 DLC for the first wave of content");
        Console.WriteLine("Press any key to continue...");

        Console.ReadLine();

        Cleanup(ctx);
    }

    private static void Cleanup(VkCtx ctx)
    {
        Vk vk = ctx.vk!;
        vk.DestroyDescriptorPool(ctx.device, ctx.descPool, null);

        vk.FreeCommandBuffers(ctx.device, ctx.compCmdPool, 1, ctx.compCmdBuf);
        vk.DestroyCommandPool(ctx.device, ctx.compCmdPool, null);
        vk.DestroyBuffer(ctx.device, ctx.compBufIn, null);
        vk.FreeMemory(ctx.device, ctx.compBufMemIn, null);
        vk.DestroyBuffer(ctx.device, ctx.compBufOut, null);
        vk.FreeMemory(ctx.device, ctx.compBufMemOut, null);

        vk.DestroyPipeline(ctx.device, ctx.compPipeline, default);
        vk.DestroyPipelineLayout(ctx.device, ctx.compPipelineLayout, default);
        vk.DestroyDescriptorSetLayout(ctx.device, ctx.compDescSetLayout, null);

        vk.DestroyCommandPool(ctx.device, ctx.compCmdPool, null);

        vk.DestroyDevice(ctx.device, null);

        if (_hasValidationLayerSupport)
            ctx.debugUtils!.DestroyDebugUtilsMessenger(ctx.instance, ctx.debugMessenger, null);

        vk.DestroyInstance(ctx.instance, null);
        vk.Dispose();
    }

    // private static void EncryptModel(VkCtx ctx, LoadResult obj)
    // {
    //     Vk vk = ctx.vk!;
    //     var points = new Vec4[1588];
    //     fixed (CommandBuffer* cmdBufPtr = &ctx.compCmdBuf)
    //     fixed (DescriptorSet* descSetLayout = &ctx.compDescSet)
    //     {
    //         CommandBufferBeginInfo beginInfo = new(StructureType.CommandBufferBeginInfo);
    //         vk.BeginCommandBuffer(ctx.compCmdBuf, beginInfo);
    //         vk.CmdBindPipeline(ctx.compCmdBuf, PipelineBindPoint.Compute, ctx.compPipeline);
    //         vk.CmdBindDescriptorSets
    //         (
    //             ctx.compCmdBuf, PipelineBindPoint.Compute,
    //             ctx.compPipelineLayout, 0, 1, descSetLayout,
    //             0, null
    //         );
    //         vk.CmdDispatch(ctx.compCmdBuf, 1588 / 32 + 1, 1, 1);
    //         vk.EndCommandBuffer(ctx.compCmdBuf);
    //         SubmitInfo submitInfo = new(commandBufferCount: 1, pCommandBuffers: cmdBufPtr);
    //         vk.QueueSubmit(ctx.queue, 1, submitInfo, default);
    //         vk.QueueWaitIdle(ctx.queue);
    //
    //         var bufSize = (ulong)(Unsafe.SizeOf<Vec4>() * 1588);
    //         void* data;
    //         ctx.vk!.MapMemory(ctx.device, ctx.compBufMemOut, 0, bufSize, 0, &data);
    //         new Span<Vec4>(data, points.Length).CopyTo(points.AsSpan());
    //         ctx.vk!.UnmapMemory(ctx.device, ctx.compBufMemOut);
    //     }
    //
    //     StringBuilder builder = new("o Model\n");
    //
    //     foreach (Vec4 v in points)
    //         builder.Append($"v {v.X:0.000000} {v.Y:0.000000} {v.Z:0.000000}\n");
    //
    //     List<uint> indices = obj.Groups.First().Faces.GetFlatIndices();
    //
    //     static void Shuffle<T>(IList<T> arr, int key)
    //     {
    //         int size = arr.Count;
    //         int[] exchanges = GetShuffleExchanges(size, key);
    //         for (int i = size - 1; i > 0; i--)
    //         {
    //             int n = exchanges[size - 1 - i];
    //             (arr[i], arr[n]) = (arr[n], arr[i]);
    //         }
    //     }
    //
    //     Shuffle(indices, 1337);
    //
    //     foreach (uint[] face in indices.Chunk(3))
    //         builder.Append($"f {face[0]} {face[1]} {face[2]}\n");
    //
    //     File.WriteAllText("Model.obj", builder.ToString());
    // }
}