using Silk.NET.Vulkan;

namespace PhysXCompute.Logic.VulcanLogic.Сonstructor
{
    public unsafe class ComputePipelineManager
    {
        private static readonly string PakPath = "shader_pak\\shaders.pak";

        public VulkanInitializer _vulkan { get; private set; }
        public bool _ownsVulkan { get; private set; }
        public Vk _vk { get; private set; }
        public Device _device { get; private set; }
        public PipelineLayout _pipelineLayout { get; private set; } // Необходим для создания пайплайна
        public DescriptorSetLayout[] _setLayouts { get; private set; } = null!;

        public ComputePipelineManager(uint[] buffersPerSet, VulkanInitializer? vulkan_ = null)
        {
            _ownsVulkan = vulkan_ == null;
            _vulkan = vulkan_ ?? new VulkanInitializer();

            _vk = _vulkan.VkApi;
            _device = _vulkan.Device;

            CreateMultiSetPipelineLayout(buffersPerSet);
        }

        /// <summary>
        /// Инициализирует Compute Pipeline из куска бинарного пакета и связывает его с кэшем.
        /// </summary>
        /// <param name="shaderInfo">Смещение и размер SPIR-V в вашем пакете</param>
        /// <param name="cachePath">Путь к файлу кэша драйвера на диске</param>
        /// <returns>Готовый объект Pipeline для выполнения на GPU</returns>
        public Pipeline InitComputePipeline((long Offset, int Size) shaderInfo, string cachePath)
        {
            PipelineCache pipelineCache = CreatePipelineCache(cachePath);
            Pipeline computePipeline;

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes("main\0");

            fixed (byte* pName = nameBytes)
            {
                byte[] testSpirvBytes = LoadChunk(shaderInfo);
                ShaderModule shaderModule = CreateShaderModule(testSpirvBytes);

                PipelineShaderStageCreateInfo stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = pName,
                    PSpecializationInfo = null
                };

                ComputePipelineCreateInfo pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageInfo,
                    Layout = _pipelineLayout
                };

                Result result = _vk.CreateComputePipelines(_device, pipelineCache, 1, &pipelineInfo, null, &computePipeline);

                if (result != Result.Success)
                {
                    throw new Exception($"Не удалось создать Compute Pipeline: {result}");
                }

                Console.WriteLine($"[GPU] Пайплайн успешно создан (Смещение: {shaderInfo.Offset}, Размер: {shaderInfo.Size} байт)!");

                // 2. Если файла кэша не было (или он обновился), сохраняем актуальный кэш драйвера на диск
                byte[] updatedCacheBytes = GetPipelineBytes(_vk, _device, pipelineCache);
                if (updatedCacheBytes.Length > 0)
                {
                    bool needWrite = true;

                    if (File.Exists(cachePath))
                    {
                        byte[] existingCacheBytes = File.ReadAllBytes(cachePath);

                        if (updatedCacheBytes.Length == existingCacheBytes.Length &&
                            System.MemoryExtensions.SequenceEqual((ReadOnlySpan<byte>)updatedCacheBytes, existingCacheBytes))
                        {
                            needWrite = false;
                        }
                    }

                    if (needWrite)
                    {
                        File.WriteAllBytes(cachePath, updatedCacheBytes);
                        Console.WriteLine($"[Disk] Кэш НЕ совпал или отсутствовал. Новый нативный кэш драйвера сохранен в {cachePath}");
                    }
                    else
                    {
                        Console.WriteLine($"[Disk] Кэш совпал! Файл на диске актуален, перезапись не требуется.");
                    }
                }

                _vk.DestroyShaderModule(_device, shaderModule, null);
                _vk.DestroyPipelineCache(_device, pipelineCache, null);

                return computePipeline;
            }
        }


        /// <summary>
        /// Ваша функция: Достает байты нативного машинного кода из кэша Vulkan
        /// </summary>
        public byte[] GetPipelineBytes(Vk vk, Device device, PipelineCache pipelineCache)
        {
            nuint cacheSize = 0;
            Result result = vk.GetPipelineCacheData(device, pipelineCache, &cacheSize, null);

            if (result != Result.Success || cacheSize == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] pipelineBytes = new byte[(int)cacheSize];

            fixed (byte* pPipelineBytes = pipelineBytes)
            {
                result = vk.GetPipelineCacheData(device, pipelineCache, &cacheSize, pPipelineBytes);
            }

            if (result != Result.Success)
            {
                throw new Exception($"Не удалось извлечь байты пайплайна: {result}");
            }

            return pipelineBytes;
        }

        /// <summary>
        /// Вспомогательный метод: превращает массив байт в VkShaderModule
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private ShaderModule CreateShaderModule(byte[] bytes)
        {
            fixed (byte* pBytes = bytes)
            {
                ShaderModuleCreateInfo createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)bytes.Length,
                    PCode = (uint*)pBytes // Хэндл Си ожидает указатель на 32-битные слова
                };

                ShaderModule module;
                Result result = _vk.CreateShaderModule(_device, &createInfo, null, &module);
                if (result != Result.Success) throw new Exception($"Ошибка создания ShaderModule: {result}");
                return module;
            }
        }

        /// <summary>
        /// Вспомогательный метод: загружает существующий .bin файл с диска в объект кэша Vulkan
        /// </summary>
        /// <param name="cachePath"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private PipelineCache CreatePipelineCache(string cachePath)
        {
            PipelineCacheCreateInfo cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            if (File.Exists(cachePath))
            {
                byte[] cacheData = File.ReadAllBytes(cachePath);
                cacheInfo.InitialDataSize = (nuint)cacheData.Length;

                fixed (byte* pCacheData = cacheData)
                {
                    cacheInfo.PInitialData = pCacheData;

                    PipelineCache cache;
                    Result result = _vk.CreatePipelineCache(_device, &cacheInfo, null, &cache);
                    if (result != Result.Success) throw new Exception($"Ошибка загрузки PipelineCache из файла: {result}");

                    Console.WriteLine($"[Disk] Кэш успешно загружен из файла {cachePath}");
                    return cache;
                }
            }
            else
            {
                // Если файла нет, создаем пустой кэш
                PipelineCache cache;
                Result result = _vk.CreatePipelineCache(_device, &cacheInfo, null, &cache);
                if (result != Result.Success) throw new Exception($"Ошибка создания пустого PipelineCache: {result}");

                Console.WriteLine("[Disk] Файл кэша не найден. Создан новый пустой PipelineCache.");
                return cache;
            }
        }

        /// <summary>
        /// Создает PipelineLayout для Compute-шейдера, поддерживающий несколько независимых Descriptor Sets.
        /// </summary>
        /// <param name="buffersPerSet">Массив чисел. Каждое число — количество буферов в конкретном сете. 
        /// Пример: new uint[] { 2, 1 } означает: в Set=0 будет 2 буфера, в Set=1 будет 1 буфер.</param>
        /// <returns>Готовый PipelineLayout и массив созданных схем наборов (DescriptorSetLayout[])</returns>
        public void CreateMultiSetPipelineLayout(uint[] buffersPerSet)
        {
            uint setCount = (uint)buffersPerSet.Length;
            DescriptorSetLayout[] setLayouts = new DescriptorSetLayout[setCount];

            for (int setIdx = 0; setIdx < setCount; setIdx++)
            {
                uint bufferCount = buffersPerSet[setIdx];

                DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[(int)bufferCount];
                for (uint b = 0; b < bufferCount; b++)
                {
                    bindings[b] = new DescriptorSetLayoutBinding
                    {
                        Binding = b,                                        // Порядковый индекс ячейки внутри текущего набора (Set).
                        DescriptorType = DescriptorType.StorageBuffer,      // Тип ресурса, который будет лежать в этой ячейке.
                        DescriptorCount = 1,                                // Количество ресурсов на этой ячейке. 
                        StageFlags = ShaderStageFlags.ComputeBit            // Указывает маску стадий конвейера, которые имеют физический доступ к этой ячейке.
                    };
                }

                DescriptorSetLayoutCreateInfo setLayoutInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,    // Указывает драйверу точный тип этой структуры.
                    BindingCount = bufferCount,                             // Указывает общее количество bindings, которые будут находиться в этом шкафу.
                    PBindings = bindings                                    // Передает Vulkan прямой указатель в памяти на массив с описанием самих полок.
                };

                DescriptorSetLayout currentSetLayout;
                Result result = _vk.CreateDescriptorSetLayout(_device, &setLayoutInfo, null, &currentSetLayout);
                if (result != Result.Success) throw new Exception($"Не удалось создать DescriptorSetLayout для Set {setIdx}: {result}");

                setLayouts[setIdx] = currentSetLayout;
            }

            PipelineLayout pipelineLayout;

            fixed (DescriptorSetLayout* pSetLayouts = setLayouts)
            {
                PipelineLayoutCreateInfo pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,         // Указывает драйверу точный тип этой структуры.
                    SetLayoutCount = setCount,                              // Указывает общее количество наборов дескрипторов, которые использует шейдер.
                    PSetLayouts = pSetLayouts,                              // Передает Vulkan прямой указатель в оперативной памяти на массив чертежей наших шкафов.
                    PushConstantRangeCount = 0,                             // Указывает количество диапазонов Push Constants, используемых в шейдере.
                    PPushConstantRanges = null                              // Передает указатель на массив с описанием диапазонов Push-констант.
                };

                Result result = _vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, &pipelineLayout);
                if (result != Result.Success) throw new Exception($"Не удалось создать многонаборный PipelineLayout: {result}");
            }

            Console.WriteLine($"[Vulkan OK] PipelineLayout успешно создан! Наборов: {setCount}.");

            _pipelineLayout = pipelineLayout;
            _setLayouts = setLayouts;
        }

        private static byte[] LoadChunk((long Offset, int Size) shaderLocation)
        {
            using var stream = new FileStream(PakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            stream.Seek(shaderLocation.Offset, SeekOrigin.Begin);

            return reader.ReadBytes(shaderLocation.Size);
        }

        public void Dispose()
        {
            Console.WriteLine("[Vulkan] Начало очистки ресурсов ComputePipelineManager...");

            if (_setLayouts != null)
            {
                foreach (var setLayout in _setLayouts)
                {
                    if (setLayout.Handle != 0)
                    {
                        _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                    }
                }
                _setLayouts = null!;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_ownsVulkan && _vulkan != null)
            {
                Console.WriteLine("[Vulkan] Менеджер был владельцем VulkanInitializer. Вызываем глобальный Cleanup...");
                _vulkan.Cleanup();
                _vulkan = null!;
            }

            Console.WriteLine("[Vulkan SUCCESS] Все ресурсы ComputePipelineManager успешно освобождены!");
        }
    }
}