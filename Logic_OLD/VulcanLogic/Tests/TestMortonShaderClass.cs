using PhysX.Logic.Сonstructor;
using PhysXCompute.Logic.VulcanLogic.Сonstructor;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Text;

namespace PhysXCompute.Logic.VulcanLogic.Tests
{
    public class TestMortonShaderClass
    {
        static unsafe void TestMortonShader(VulkanInitializer vulkan, ComputePipelineManager manager)
        {
            Console.WriteLine("[ТЕСТ] Инициализация сценария TestMorton...");

            // 1. Инициализируем пайплайн Vulkan
            string cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shader_cache\\morton_cache.bin");
            // Передаем координаты вашего скомпилированного шейдера из файла Shaders.TestMorton
            Pipeline pipeline = manager.InitComputePipeline((Shaders.TestMorton.Offset, Shaders.TestMorton.Size), cachePath);

            // 2. Создаем логгер буферов и инициализируем пул дескрипторов
            BuffersManager buffersLog = new BuffersManager(vulkan);
            buffersLog.InitDescriptorPool();

            // ==========================================
            // ПОДГОТОВКА ДАННЫХ ДЛЯ СЦЕНЫ И ОБЪЕКТОВ
            // ==========================================
            uint objectCount = 4;

            // Границы сцены: от (0,0,0) до (10,10,10). Размер = 10. InvSize = 1.0 / 10.0 = 0.1f
            SceneBounds boundsConfig = new SceneBounds
            {
                SceneMin = new Float3(0f, 0f, 0f),
                SceneInvSize = new Float3(0.1f, 0.1f, 0.1f),
                ObjectCount_SceneBounds = objectCount
            };

            // Тестовые объекты в разных точках сцены
            ObjectData[] inputObjects = new ObjectData[objectCount];

            // Объект 0: Центр в (1, 1, 1) -> Близко к началу координат (маленький код Мортона)
            inputObjects[0].Bounds = new AABB { MinPos = new Float3(0.5f, 0.5f, 0.5f), MaxPos = new Float3(1.5f, 1.5f, 1.5f) };

            // Объект 1: Центр в (9, 9, 9) -> Близко к концу сцены (большой код Мортона)
            inputObjects[1].Bounds = new AABB { MinPos = new Float3(8.5f, 8.5f, 8.5f), MaxPos = new Float3(9.5f, 9.5f, 9.5f) };

            // Объект 2: Центр в (5, 5, 5) -> Центр сцены
            inputObjects[2].Bounds = new AABB { MinPos = new Float3(4.5f, 4.5f, 4.5f), MaxPos = new Float3(5.5f, 5.5f, 5.5f) };

            // Объект 3: Центр в (2, 8, 4)
            inputObjects[3].Bounds = new AABB { MinPos = new Float3(1.5f, 7.5f, 3.5f), MaxPos = new Float3(2.5f, 8.5f, 4.5f) };

            // ==========================================
            // ИНИЦИАЛИЗАЦИЯ И СВЯЗЫВАНИЕ БУФЕРОВ
            // ==========================================
            // Создаем экземпляр автосгенерированного класса обертки
            using var mortonInstance = new TestMortonInstance(vulkan, buffersLog, pipeline, manager._pipelineLayout);

            // В соответствии с вашим HLSL кодом выделяем память:
            // Порядок в массиве params (int Size, DescriptorType Type):
            // Биндинг 0 (SceneBounds) -> UniformBuffer
            // Биндинг 1 (Out_MortonPairs) -> StorageBuffer
            // Биндинг 2 (MovedObjects) -> StorageBuffer
            mortonInstance.AllocateBuffers(
                (sizeof(SceneBounds), DescriptorType.UniformBuffer),                        // Binding 0
                ((int)objectCount * sizeof(MortonPair), DescriptorType.StorageBuffer),      // Binding 1
                ((int)objectCount * sizeof(ObjectData), DescriptorType.StorageBuffer)       // Binding 2
            );

            // Загружаем подготовленные массивы в память видеокарты
            // Укажите массив в качестве Span в сгенерированные методы
            mortonInstance.WriteBinding0(boundsConfig);
            mortonInstance.WriteBinding2(new ReadOnlySpan<ObjectData>(inputObjects));

            Console.WriteLine("[ТЕСТ] Данные загружены. Вызов Dispatch...");

            // ==========================================
            // ЗАПУСК ВЫЧИСЛЕНИЙ НА GPU
            // ==========================================
            // Название метода сгенерировано автоматически по принципу ИмяФайла_Kernel
            mortonInstance.Dispatch_TestMorton_Kernel(objectCount);

            Console.WriteLine("[ТЕСТ] Расчет завершен. Чтение результатов...");

            // ==========================================
            // ВАЛИДАЦИЯ И ВЫВОД РЕЗУЛЬТАТОВ
            // ==========================================
            // Читаем сгенерированные пары из Binding 1
            MortonPair[] outputPairs = mortonInstance.ReadBinding1<MortonPair>((int)objectCount);

            Console.WriteLine("\n--- РЕЗУЛЬТАТЫ ГЕНЕРАЦИИ КОДОВ МОРТОНА ---");
            for (int i = 0; i < (int)objectCount; i++)
            {
                Console.WriteLine($"Объект ID: {outputPairs[i].ObjectID} | Полученный Код Мортона: {outputPairs[i].MortonCode} (В бинарном виде: {Convert.ToString(outputPairs[i].MortonCode, 2).PadLeft(30, '0')})");
            }

            // Простая логическая проверка: Объект в (9,9,9) должен иметь код Мортона значительно больше, чем объект в (1,1,1)
            if (outputPairs[1].MortonCode > outputPairs[0].MortonCode && outputPairs[0].MortonCode != 0)
            {
                Console.WriteLine("\n[УСПЕХ] Тест пройден! Коды распределены корректно в 3D пространстве.");
            }
            else
            {
                Console.WriteLine("\n[ОШИБКА] Тест провален. Коды Мортона посчитаны неверно или буферы вернули нули.");
            }

            // Очищаем вспомогательную инфраструктуру буферов
            buffersLog.Clean();
        }
    }
}
