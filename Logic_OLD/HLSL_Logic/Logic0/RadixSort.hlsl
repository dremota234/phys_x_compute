#pragma region Radix Sort

// Out_MortonPairs

// Константы конфигурации
#define THREADS_PER_BLOCK 256
#define BITS_PER_PASS 4
#define NUM_BUCKETS 16 // 2^4

RWStructuredBuffer<uint> OutputBuffer    : register(u1);

// Вспомогательные буферы для межшейдерного обмена
RWStructuredBuffer<uint> BlockHistograms : register(u2); 
RWStructuredBuffer<uint> GlobalOffsets   : register(u3);

cbuffer SortSettings : register(b0)
{
    uint BitShift;       // Текущий сдвиг (0, 4, 8, 12...)
    uint TotalElements;  // Размер массива чисел
    uint TotalBlocks;    // Общее число блоков (Ceil(TotalElements / 256))
};
groupshared uint s_Histogram[NUM_BUCKETS];

[numthreads(THREADS_PER_BLOCK, 1, 1)]
void CS_Upsweep(
    uint3 Gid : SV_GroupID, 
    uint3 tid : SV_GroupThreadID,
    uint3 DTid : SV_DispatchThreadID)
{
    // 1. Быстрое обнуление гистограммы блока
    if (tid.x < NUM_BUCKETS) 
    {
        s_Histogram[tid.x] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    // 2. Чтение элемента и определение его корзины
    uint globalIdx = DTid.x;
    uint bucket = 0xFFFFFFFF;
    
    if (globalIdx < TotalElements)
    {
        uint element = Out_MortonPairs[globalIdx].mortonCode;
        bucket = (element >> BitShift) & 0xF;
    }

    // 3. Оптимизация через Wave-интринсики
    // Потоки внутри одной волны (варпа) суммируют свои данные без обращений к памяти
    [unroll]
    for (uint b = 0; b < NUM_BUCKETS; b++)
    {
        bool isTargetBucket = (bucket == b);
        // Сколько потоков в текущей волне (32 или 64) выбрали эту корзину
        uint waveCount = WaveActiveCountBits(isTargetBucket);
        
        // Только первый поток из волны, у которого совпал бакет, пишет сумму в shared
        if (waveCount > 0 && WaveIsFirstLane())
        {
            InterlockedAdd(s_Histogram[b], waveCount);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // 4. Запись гистограммы этого блока в глобальную память
    if (tid.x < NUM_BUCKETS)
    {
        uint blockIdx = Gid.x;
        // Раскладка: [Корзина 0: Блок 0..TotalBlocks-1][Корзина 1: Блок 0..TotalBlocks-1]...
        BlockHistograms[tid.x * TotalBlocks + blockIdx] = s_Histogram[tid.x];
    }
}
groupshared uint s_ScanMem[THREADS_PER_BLOCK * 2];

[numthreads(THREADS_PER_BLOCK, 1, 1)]
void CS_Scan(uint3 tid : SV_GroupThreadID)
{
    // Суммарный размер таблицы гистограмм = Число блоков * 16 корзин
    uint totalHistogramEntries = TotalBlocks * NUM_BUCKETS;
    uint globalOffsetAccumulator = 0;

    // Сканируем блоками по THREADS_PER_BLOCK элементов.
    // Благодаря раскладке [Корзина][Блок] из CS_Upsweep этот обычный линейный
    // scan уже даёт правильную семантику: exclusiveSum для (корзина d, блок b) =
    // (сумма всех элементов корзин < d по всем блокам) + (сумма корзины d в блоках < b).
    for (uint offset = 0; offset < totalHistogramEntries; offset += THREADS_PER_BLOCK)
    {
        uint localIdx = tid.x;
        uint globalIdx = offset + localIdx;

        // 1. Загружаем данные в shared память
        s_ScanMem[localIdx] = (globalIdx < totalHistogramEntries) ? BlockHistograms[globalIdx] : 0;
        GroupMemoryBarrierWithGroupSync();

        // 2. Инкрементный префиксный скан внутри блока (Kogge-Stone / Hillis-Steele алгоритм)
        uint sum = s_ScanMem[localIdx];
        [unroll]
        for (uint stride = 1; stride < THREADS_PER_BLOCK; stride *= 2)
        {
            uint val = 0;
            if (localIdx >= stride)
            {
                val = s_ScanMem[localIdx - stride];
            }
            GroupMemoryBarrierWithGroupSync();
            sum += val;
            s_ScanMem[localIdx] = sum;
            GroupMemoryBarrierWithGroupSync();
        }

        // 3. Вычисляем эксклюзивный скан и добавляем смещение от прошлых итераций цикла
        if (globalIdx < totalHistogramEntries)
        {
            uint inclusiveSum = s_ScanMem[localIdx];
            uint exclusiveSum = inclusiveSum - BlockHistograms[globalIdx];
            
            // Записываем финальный адрес для этого блока и этой корзины
            GlobalOffsets[globalIdx] = exclusiveSum + globalOffsetAccumulator;
        }

        // Обновляем аккумулятор общего смещения для следующей итерации цикла
        GroupMemoryBarrierWithGroupSync();
        globalOffsetAccumulator += s_ScanMem[THREADS_PER_BLOCK - 1];
        GroupMemoryBarrierWithGroupSync();
    }
}
groupshared uint s_BlockOffsets[NUM_BUCKETS];

// В HLSL groupshared-переменные обязаны объявляться глобально, а не внутри функции.
groupshared uint s_WaveOffsets[NUM_BUCKETS][8]; // Максимум 8 волн в блоке из 256 потоков

[numthreads(THREADS_PER_BLOCK, 1, 1)]
void CS_Downsweep(
    uint3 Gid : SV_GroupID, 
    uint3 tid : SV_GroupThreadID,
    uint3 DTid : SV_DispatchThreadID)
{
    uint blockIdx = Gid.x;

    // 1. Кешируем стартовые глобальные адреса корзин для нашего блока
    // ИСПРАВЛЕНО: индексация под новую раскладку [Корзина][Блок].
    if (tid.x < NUM_BUCKETS)
    {
        s_BlockOffsets[tid.x] = GlobalOffsets[tid.x * TotalBlocks + blockIdx];
    }
    GroupMemoryBarrierWithGroupSync();

    uint globalIdx = DTid.x;
    if (globalIdx >= TotalElements) return;

    // 2. Читаем элемент
    MortonPair pairData = Out_MortonPairs[globalIdx];
    uint sortKey = pairData.mortonCode;
    uint element = pairData.objectID;
    uint bucket = (sortKey >> BitShift) & 0xF;

    // 3. Нахождение локального индекса внутри блока БЕЗ памяти и циклов
    uint localRank = 0;
    
    [unroll]
    for (uint b = 0; b < NUM_BUCKETS; b++)
    {
        bool isCurrentBucket = (bucket == b);
        
        // Интринсик возвращает количество потоков в текущей WAVE,
        // у которых индекс меньше текущего И условие true.
        if (isCurrentBucket)
        {
            localRank = WavePrefixCountBits(isCurrentBucket);
        }
    }

    // 4. Синхронизируем локальные сдвиги между волнами (если внутри блока > 1 волны)
    // Для этого временно используем shared-память, чтобы прибавить сдвиги от прошлых волн
    // (Поскольку THREADS_PER_BLOCK = 256, а Wave обычно 32 или 64)
    uint waveIdx = tid.x / WaveGetLaneCount();
    
    if (WaveIsFirstLane())
    {
        // Каждая волна считает сколько у нее элементов в каждом бакете всего
        [unroll]
        for (uint b = 0; b < NUM_BUCKETS; b++)
        {
            s_WaveOffsets[b][waveIdx] = WaveActiveCountBits(bucket == b);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // Считаем префиксную сумму сдвигов между волнами для нашего бакета
    uint intraWaveOffset = 0;
    for (uint w = 0; w < waveIdx; w++)
    {
        intraWaveOffset += s_WaveOffsets[bucket][w];
    }

    // 5. Запись в финальную отсортированную ячейку
    uint finalIndex = s_BlockOffsets[bucket] + localRank + intraWaveOffset;
    OutputBuffer[finalIndex] = element;
}


#pragma endregion