using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PhysXCompute.Logic.VulcanLogic.Сonstructor
{
    public unsafe class VulkanInitializer
    {
        public VulkanInitializer() => Initialize(); 

        /// <summary>
        /// Свойство для хранения объекта самого API Vulkan (через него вызываются все функции)
        /// </summary>
        public Vk VkApi { get; private set; } = null!;

        /// <summary>
        /// Свойство для Экземпляра Vulkan (связующее звено между приложением и драйвером ОС)
        /// </summary>
        public Instance Instance { get; private set; }

        /// <summary>
        /// Свойство для Физического устройства (ссылка на саму железную видеокарту в ПК)
        /// </summary>
        public PhysicalDevice PhysicalDevice { get; private set; }

        /// <summary>
        /// Свойство для Логического устройства (интерфейс, через который мы управляем видеокартой)
        /// </summary>
        public Device Device { get; private set; }

        /// <summary>
        /// Свойство для 
        /// </summary>
        public Queue Queue { get; private set; }

        /// <summary>
        /// Пошагово поднимет Vulkan с нуля
        /// </summary>
        public void Initialize()
        {
            VkApi = Vk.GetApi();    // Находим и загружаем системную библиотеку vulkan-1.dl

            // Создаем структуру с информацией о нашей программе для драйвера видеокарты
            ApplicationInfo appInfo = new ApplicationInfo   
            {
                SType = StructureType.ApplicationInfo,                                  // указываем драйверу точный тип этой структуры
                PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("PhysX"),         // Память в куче Си под имя приложения и передаем указатель byte*
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),                           // Версия приложения в понятный для Vulkan формат uint
                PEngineName = (byte*)Marshal.StringToHGlobalAnsi("PhysXCompute"),       // Память под имя движка ("No Engine" — без движка)
                EngineVersion = Vk.MakeVersion(1, 0, 0),                                // Версия движка в понятный для Vulkan формат uint
                ApiVersion = Vk.Version10                                               // Задаем версию спецификации Vulkan 1.0, которую мы требуем от GPU
            };

            // Создаем структуру с глобальными настройками для создания Экземпляра (Instance)
            InstanceCreateInfo createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,   // Указываем драйверу, что это структура настроек инстанса
                PApplicationInfo = &appInfo,                // Передаем адрес (&) заполненной выше структуры с информацией о приложении
                EnabledExtensionCount = 0,                  // Количество глобальных расширений
                PpEnabledExtensionNames = null,             // Массив имен расширений (отсутствует)
                EnabledLayerCount = 0,                      // Количество слоев отладки/валидации
                PpEnabledLayerNames = null                  // Массив имен слоев отладки пуст
            };

            Instance instance;                                                              // Создаем пустую переменную, в которую Vulkan запишет дескриптор инстанса
            
            Result result = VkApi.CreateInstance(&createInfo, null, &instance);             // Передаем: адрес настроек, null (без кастомных аллокаторов) и адрес переменной

            if (result != Result.Success) throw new Exception($"Не удалось создать Instance Vulkan: {result}");
            
            Instance = instance;                                                            // Сохраняем созданный инстанс в свойство класса
            
            Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);                          // Освобождаем неуправляемую память Си, которую мы занимали под строку "MyVulkanApp"
            Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);                               // Освобождаем неуправляемую память Си, которую мы занимали под строку "No Engine"

            uint deviceCount = 0;                                                           // Количество видеокарт в системе
            VkApi.EnumeratePhysicalDevices(Instance, &deviceCount, null);                   // Считаем число видеокарт

            if (deviceCount == 0) throw new Exception("Не найдено видеокарт с поддержкой Vulkan!");

            PhysicalDevice* physicalDevices = stackalloc PhysicalDevice[(int)deviceCount];  // Временный массив под видеокарты в быстром стеке памяти CPU
            VkApi.EnumeratePhysicalDevices(Instance, &deviceCount, physicalDevices);        // Указатель на массив, Vulkan заполняет его видеокартами 
            PhysicalDevice = physicalDevices[0];                                            // первая доступную видеокарту из списка (id 0)

            float queuePriority = 1.0f;                                                     // Приоритет для очереди выполнения команд (от 0.0 до 1.0)

            DeviceQueueCreateInfo queueCreateInfo = new DeviceQueueCreateInfo               // Видеокарта принимает задачи через очереди. Описываем настройки для создания одной очереди.
            {
                SType = StructureType.DeviceQueueCreateInfo,    // тип структуры — настройки очереди устройства
                QueueFamilyIndex = 0,                           // Индекс семейства очередей. 0 — на 99% карт это универсальная очередь (графика + вычисления)
                QueueCount = 1,                                 // Количество линий для отправки Compute-задач
                PQueuePriorities = &queuePriority               // Адрес переменной, где хранится приоритет (1.0f)
            };

            PhysicalDeviceFeatures deviceFeatures = new PhysicalDeviceFeatures();           // Пустая структуру для включения фич GPU (геометрия, тесселяция и т.д.).

            DeviceCreateInfo deviceCreateInfo = new DeviceCreateInfo                        // Глобальная структура настроек для создания Device
            {
                SType = StructureType.DeviceCreateInfo, // Указываем драйверу тип структуры — настройки логического устройства
                QueueCreateInfoCount = 1,               // Указываем, что передаем блок настроек только для 1 семейства очередей
                PQueueCreateInfos = &queueCreateInfo,   // Передаем адрес структуры настроек нашей очереди, которую заполнили выше
                PEnabledFeatures = &deviceFeatures,     // Передаем адрес структуры с фичами видеокарты
                EnabledExtensionCount = 0,              // Указываем количество расширений устройства (для вычислений/шейдеров ставим 0)
                PpEnabledExtensionNames = null          // Массив имен расширений устройства пуст
            };

            Device device;
            result = VkApi.CreateDevice(PhysicalDevice, &deviceCreateInfo, null, &device);

            if (result != Result.Success) throw new Exception($"Не удалось создать логическое устройство Device: {result}");

            Device = device;

            Queue localQueue;
            VkApi.GetDeviceQueue(Device, 0, 0, &localQueue);

            bool queueObtained = localQueue.Handle != 0;
            if (!queueObtained) throw new Exception("Критическая ошибка: Не удалось получить доступ к очереди GPU!");

            Queue = localQueue;

            Console.WriteLine("Vulkan успешно инициализирован! Все типы переменных получены.");
        }

        /// <summary>
        /// Очистка ресурсов Vulkan
        /// </summary>
        public void Cleanup()
        {
            if (Device.Handle != 0) VkApi.DestroyDevice(Device, null);
            if (Instance.Handle != 0) VkApi.DestroyInstance(Instance, null);
        }
    }
}
