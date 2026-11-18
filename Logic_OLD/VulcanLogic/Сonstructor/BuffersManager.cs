using Silk.NET.Vulkan;

namespace PhysXCompute.Logic.VulcanLogic.Сonstructor
{
    public class BuffersManager
    {
        public BuffersManager(VulkanInitializer vulkan)
        {
            _device = vulkan.Device;
            _vk = vulkan.VkApi;
            _physicalDevice = vulkan.PhysicalDevice;
            _queryPool = vulkan.Queue;
        }

        private Device _device;
        private Vk _vk = null!;
        private PhysicalDevice _physicalDevice;
        private Queue _queryPool;

        private CommandPool _commandPool;
        private CommandBuffer _cmdBuffer;
        private Fence _fence;
        private DescriptorPool _descriptorPool;

        /// <summary>
        /// Универсальный метод для низкоуровневого создания буферов в Vulkan API.
        /// Выделяет виртуальный дескриптор ресурса, запрашивает аппаратные требования,
        /// находит нужный тип физической памяти на GPU, выделяет её, связывает с буфером
        /// и побайтово копирует начальные данные из оперативной памяти (CPU) в видеопамять (GPU).
        /// </summary>
        /// <typeparam name="T">Тип данных, размещаемых в буфере. Ограничен структурами unmanaged (без ссылочных типов внутри).</typeparam>
        /// <param name="data">Начальный набор данных в виде неизменяемого среза памяти (ReadOnlySpan).</param>
        /// <param name="usage">Флаги назначения буфера (например, StorageBufferBit для Compute-шейдеров, VertexBufferBit для геометрии).</param>
        /// <param name="preferredProperties">Физические свойства памяти (например, HostVisibleBit для доступа со стороны CPU, HostCachedBit для быстрого чтения).</param>
        /// <returns>Возвращает кортеж из созданного логического буфера и дескриптора выделенной физической памяти устройства.</returns>
        public unsafe (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateDeviceBuffer<T>(
            ReadOnlySpan<T> data, BufferUsageFlags usage, MemoryPropertyFlags preferredProperties) where T : unmanaged
        {
            ulong bufferSize = (ulong)(data.Length * sizeof(T));

            BufferCreateInfo bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = bufferSize,
                Usage = usage,
                SharingMode = SharingMode.Exclusive
            };

            Silk.NET.Vulkan.Buffer buffer;
            _vk.CreateBuffer(_device, &bufferInfo, null, &buffer);

            MemoryRequirements memReq;
            _vk.GetBufferMemoryRequirements(_device, buffer, &memReq);

            uint bits = memReq.MemoryTypeBits;
            uint memoryTypeIndex = FindMemoryType(bits, preferredProperties);

            MemoryAllocateInfo allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = memoryTypeIndex
            };

            DeviceMemory memory;

            _vk.AllocateMemory(_device, &allocInfo, null, &memory);
            _vk.BindBufferMemory(_device, buffer, memory, 0);

            void* pData;
            _vk.MapMemory(_device, memory, 0, bufferSize, 0, &pData);

            fixed (T* pSource = data)
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pData, pSource, (uint)bufferSize);

            _vk.UnmapMemory(_device, memory);

            return (buffer, memory);
        }

        /// <summary>
        /// Низкоуровневый вспомогательный метод фильтрации и поиска подходящего аппаратного типа памяти GPU.
        /// Сверяет битовую маску ограничений конкретного ресурса со свойствами физических куч памяти, распаянных на плате устройства.
        /// </summary>
        /// <param name="typeFilter">Битовая маска из GetBufferMemoryRequirements, указывающая, какие индексы памяти физически совместимы с буфером.</param>
        /// <param name="properties">Битовая маска свойств, которые нам строго необходимы от памяти (например, HostVisibleBit | HostCoherentBit).</param>
        /// <returns>Возвращает системный индекс (номер ячейки) кучи памяти, готовой к аллокации.</returns>
        private unsafe uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
        {
            PhysicalDeviceMemoryProperties memProperties;

            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProperties); // Опрашиваем видеокарту о доступных типах памяти

            for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
            {
                if ((typeFilter & (1 << (int)i)) != 0 &&
                    (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties) return i; // Нашли идеальный индекс
            }

            throw new Exception("Не удалось найти подходящий тип памяти для буфера!");
        }

        #region Create Buffers

        public (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateRWStructuredBuffer<T>(ReadOnlySpan<T> initialData) where T : unmanaged
        {
            return CreateDeviceBuffer(
                initialData,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            );
        }
        public (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateEmptyRWStructuredBuffer<T>(int elementCount) where T : unmanaged
        {
            return CreateDeviceBuffer(
                new ReadOnlySpan<T>(new T[elementCount]),
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit
            );
        }
        #endregion

        #region Write - Read

        public unsafe void WriteToDeviceMemory<T>(DeviceMemory memory, ulong bufferSize, ReadOnlySpan<T> newData) where T : unmanaged
        {
            void* pData;
            Result result = _vk.MapMemory(_device, memory, 0, bufferSize, 0, &pData);
            if (result != Result.Success) throw new Exception($"Ошибка MapMemory при записи: {result}");

            fixed (T* pSource = newData)
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pData, pSource, (uint)bufferSize);

            _vk.UnmapMemory(_device, memory);
        }

        public unsafe T[] ReadFromDeviceMemory<T>(DeviceMemory memory, ulong bufferSize, int elementCount) where T : unmanaged
        {
            T[] resultData = new T[elementCount];

            void* pData;
            Result result = _vk.MapMemory(_device, memory, 0, bufferSize, 0, &pData);
            if (result != Result.Success) throw new Exception($"Ошибка MapMemory при чтении: {result}");

            fixed (T* pDest = resultData)
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pDest, pData, (uint)bufferSize);

            _vk.UnmapMemory(_device, memory);

            return resultData;
        }

        public unsafe void WriteToDeviceMemory<T>(
            DeviceMemory memory,
            ReadOnlySpan<T> newData,
            int elementOffset = 0,
            MemoryPropertyFlags properties = MemoryPropertyFlags.HostCoherentBit) where T : unmanaged
        {
            if (newData.Length == 0) return;

            ulong bytesToWrite = (ulong)(newData.Length * sizeof(T));
            ulong byteOffset = (ulong)(elementOffset * sizeof(T));

            void* pData;
            Result result = _vk.MapMemory(_device, memory, byteOffset, bytesToWrite, 0, &pData);
            if (result != Result.Success) throw new Exception($"Ошибка MapMemory при записи: {result}");

            fixed (T* pSource = newData)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pData, pSource, (uint)bytesToWrite);
            }

            if (!properties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                MappedMemoryRange range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = memory,
                    Offset = byteOffset,
                    Size = bytesToWrite
                };
                _vk.FlushMappedMemoryRanges(_device, 1, &range);
            }

            _vk.UnmapMemory(_device, memory);
        }

        public unsafe T[] ReadFromDeviceMemory<T>(
            DeviceMemory memory,
            int elementCount,
            int elementOffset = 0,
            MemoryPropertyFlags properties = MemoryPropertyFlags.HostCoherentBit) where T : unmanaged
        {
            if (elementCount <= 0) return Array.Empty<T>();

            ulong bytesToRead = (ulong)(elementCount * sizeof(T));
            ulong byteOffset = (ulong)(elementOffset * sizeof(T));

            T[] resultData = new T[elementCount];

            if (!properties.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                MappedMemoryRange range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = memory,
                    Offset = byteOffset,
                    Size = bytesToRead
                };
                _vk.InvalidateMappedMemoryRanges(_device, 1, &range);
            }

            void* pData;
            Result result = _vk.MapMemory(_device, memory, byteOffset, bytesToRead, 0, &pData);
            if (result != Result.Success) throw new Exception($"Ошибка MapMemory при чтении: {result}");

            fixed (T* pDest = resultData)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlock(pDest, pData, (uint)bytesToRead);
            }

            _vk.UnmapMemory(_device, memory);

            return resultData;
        }

        #endregion

        public unsafe (DescriptorSetLayout Layout, DescriptorSet Set) CreateDescriptorSet(
            params (uint Binding, Silk.NET.Vulkan.Buffer Buffer, ulong Size, DescriptorType Type)[] bufferBindings)
        {
            
            int count = bufferBindings.Length;

            DescriptorSetLayoutBinding* layoutBindings = stackalloc DescriptorSetLayoutBinding[count];
            for (int i = 0; i < count; i++)
            {
                layoutBindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = bufferBindings[i].Binding,
                    DescriptorType = bufferBindings[i].Type,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit
                };
            }

            DescriptorSetLayoutCreateInfo layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)count,
                PBindings = layoutBindings
            };

            DescriptorSetLayout layout;
            if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, &layout) != Result.Success)
            {
                throw new Exception("Не удалось создать Descriptor Set Layout.");
            }

            DescriptorSetLayout localLayout = layout;
            DescriptorSetAllocateInfo allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &localLayout
            };

            DescriptorSet descriptorSet;
            if (_vk.AllocateDescriptorSets(_device, &allocInfo, &descriptorSet) != Result.Success)
            {
                _vk.DestroyDescriptorSetLayout(_device, layout, null);
                throw new Exception("Не удалось выделить Descriptor Set.");
            }

            WriteDescriptorSet* descriptorWrites = stackalloc WriteDescriptorSet[count];
            DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[count];

            for (int i = 0; i < count; i++)
            {
                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = bufferBindings[i].Buffer,
                    Offset = 0,
                    Range = bufferBindings[i].Size
                };

                descriptorWrites[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = bufferBindings[i].Binding,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = bufferBindings[i].Type,
                    PBufferInfo = &bufferInfos[i]
                };
            }

            _vk.UpdateDescriptorSets(_device, (uint)count, descriptorWrites, 0, null);

            return (layout, descriptorSet);
        }

        public unsafe void InitComputeInfrastructure(uint computeQueueFamilyIndex)
        {
            CommandPoolCreateInfo poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = computeQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit
            };

            fixed (CommandPool* pPool = &_commandPool)
            {
                if (_vk.CreateCommandPool(_device, &poolInfo, null, pPool) != Result.Success)
                {
                    throw new Exception("Не удалось создать Command Pool.");
                }
            }

            CommandBufferAllocateInfo cmdAllocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            fixed (CommandBuffer* pCmd = &_cmdBuffer)
            {
                _vk.AllocateCommandBuffers(_device, &cmdAllocInfo, pCmd);
            }

            FenceCreateInfo fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            fixed (Fence* pFence = &_fence)
            {
                _vk.CreateFence(_device, &fenceInfo, null, pFence);
            }
        }

        public unsafe void ExecuteCompute(
            Pipeline pipeline,
            PipelineLayout layout,
            DescriptorSet descriptorSet,
            uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1)
        {
            fixed (Fence* pFence = &_fence)
            {
                _vk.ResetFences(_device, 1, pFence);
            }

            _vk.ResetCommandBuffer(_cmdBuffer, 0);

            CommandBufferBeginInfo beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };

            _vk.BeginCommandBuffer(_cmdBuffer, &beginInfo);

            _vk.CmdBindPipeline(_cmdBuffer, PipelineBindPoint.Compute, pipeline);

            DescriptorSet localSet = descriptorSet;
            _vk.CmdBindDescriptorSets(_cmdBuffer, PipelineBindPoint.Compute, layout, 0, 1, &localSet, 0, null);

            _vk.CmdDispatch(_cmdBuffer, groupCountX, groupCountY, groupCountZ);

            _vk.EndCommandBuffer(_cmdBuffer);

            CommandBuffer localCmd = _cmdBuffer;
            SubmitInfo submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &localCmd
            };

            fixed (Fence* pFence = &_fence)
            {
                _vk.QueueSubmit(_queryPool, 1, &submitInfo, *pFence);

                _vk.WaitForFences(_device, 1, pFence, true, 2_000_000_000);
            }

            Console.WriteLine("[Менеджер] GPU успешно завершил вычисления!");
        }

        public unsafe void InitDescriptorPool()
        {
            DescriptorPoolSize poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = 10
            };

            DescriptorPoolCreateInfo poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 5,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit
            };

            fixed (DescriptorPool* pPool = &_descriptorPool)
            {
                if (_vk.CreateDescriptorPool(_device, &poolInfo, null, pPool) != Result.Success)
                {
                    throw new Exception("Не удалось создать Descriptor Pool.");
                }
            }
        }

        public unsafe void Clean()
        {
            // 1. Ждем, пока GPU завершит все текущие задачи перед очисткой (важнейший шаг защиты от вылетов)
            if (_device.Handle != 0)
            {
                _vk.DeviceWaitIdle(_device);
            }

            // 2. Уничтожаем пул дескрипторов (автоматически освобождает все выделенные из него Descriptor Sets)
            if (_descriptorPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
                _descriptorPool = default;
            }

            // 3. Освобождаем выделенный командный буфер (если пул не удаляет его автоматически)
            if (_cmdBuffer.Handle != 0 && _commandPool.Handle != 0)
            {
                var localCmd = _cmdBuffer;
                _vk.FreeCommandBuffers(_device, _commandPool, 1, &localCmd);
                _cmdBuffer = default;
            }

            // 4. Уничтожаем пул команд
            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
            }

            // 5. Уничтожаем забор (Fence) синхронизации
            if (_fence.Handle != 0)
            {
                _vk.DestroyFence(_device, _fence, null);
                _fence = default;
            }

            Console.WriteLine("[BuffersLog] Вся вычислительная инфраструктура GPU успешно очищена.");
        }
    }
}
