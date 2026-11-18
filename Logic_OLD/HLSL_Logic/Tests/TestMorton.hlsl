#pragma region Morton
#pragma kernel TestMorton_Kernel

// MovedObjects
////////////////////////////////////////////////////////////////////////////////
struct AABB
{
    float3 minPos;
    float3 maxPos;
};

struct ObjectData
{
    AABB bounds;
};

StructuredBuffer<ObjectData> MovedObjects : register(t2);

////////////////////////////////////////////////////////////////////////////////
struct MortonPair 
{
    uint mortonCode; // Сам код Мортона
    uint objectID;   // Исходный индекс объекта
}; 

cbuffer SceneBounds : register(b0)
{
    float3 SceneMin;
    float3 SceneInvSize; // 1.0 / (SceneMax - SceneMin)
    uint   ObjectCount_SceneBounds;
};

RWStructuredBuffer<MortonPair> Out_MortonPairs : register(u1);

// Вспомогательная функция: вставляет 2 нуля между каждым битом 10-битного числа
uint ExpandBits(uint v)
{
    v = (v * 0x00010001u) & 0xFF0000FFu;
    v = (v * 0x00000101u) & 0x0F00F00Fu;
    v = (v * 0x00000011u) & 0xC30C30C3u;
    v = (v * 0x00000005u) & 0x49249249u;
    return v;
}

// Генерация 32-битного кода Мортона из нормализованных координат [0, 1]
uint CalculateMorton3D(float3 center)
{
    // Нормализуем центр относительно границ всей сцены в диапазон [0, 1]
    float3 normalizedPos = (center - SceneMin) * SceneInvSize;
    
    // Масштабируем [0.0, 1.0] в диапазон 10-битного целого числа [0, 1023]
    uint x = min(max((uint)(normalizedPos.x * 1024.0f), 0u), 1023u);
    uint y = min(max((uint)(normalizedPos.y * 1024.0f), 0u), 1023u);
    uint z = min(max((uint)(normalizedPos.z * 1024.0f), 0u), 1023u);

    // Раздвигаем биты и переплетаем их
    return (ExpandBits(x) << 0) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
}

[numthreads(64, 1, 1)]
void TestMorton_Kernel(uint3 dtID : SV_DispatchThreadID)
{
    uint objIdx = dtID.x;

    if (objIdx >= ObjectCount_SceneBounds) return;          // Защита от выхода за пределы массива
    
    AABB bounds = MovedObjects[objIdx].bounds;              // 1. Получаем AABB текущего объекта
    float3 center = (bounds.minPos + bounds.maxPos) * 0.5f; // 2. Вычисляем центр объекта
    uint code = CalculateMorton3D(center);                  // 3. Считаем код Мортона
    
    MortonPair pair;                                        // 4. Записываем пару для последующей сортировки
    pair.mortonCode = code;                                 //
    pair.objectID = objIdx;                                 //
                                                            //
    Out_MortonPairs[objIdx] = pair;                         //
}
#pragma endregion