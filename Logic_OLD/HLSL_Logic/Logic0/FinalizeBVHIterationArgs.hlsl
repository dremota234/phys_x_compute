#pragma region FinalizeBVHIterationArgs

RWStructuredBuffer<uint> BVHIndirectArgs : register(u3);

// Финальный индекс корня. Читается CS_TraverseBVH напрямую как SRV — без
// единого readback на CPU за весь кадр.
RWStructuredBuffer<uint> RootNodeIndexBuffer : register(u4);

[numthreads(1, 1, 1)]
void CS_FinalizeBVHIterationArgs(uint3 DTid : SV_DispatchThreadID)
{
    uint nextActiveCount = GlobalCounters.Load(4);

    if (nextActiveCount <= 1)
    {
        // Дерево схлопнулось (или было изначально 0-1 объектов в сцене).
        // Читаем корень прямо из Out_ActiveIndices — того же буфера, куда
        // BVH_Main писал результат этой итерации. Отдельная декларация
        // для чтения не нужна: RWStructuredBuffer разрешает и запись, и
        // чтение в одном кернеле.
        if (nextActiveCount == 1)
        {
            RootNodeIndexBuffer[0] = Out_ActiveIndices[0];
        }

        // Обнуляем группы потоков — следующий DispatchIndirect(BVH_Main)
        // не выполнит ни одного потока. Останавливает бесконечное
        // самокопирование последней активной ноды (см. разбор бага в чате).
        BVHIndirectArgs[0] = 0;
    }
    else
    {
        BVHIndirectArgs[0] = (nextActiveCount + 63) / 64; // threadGroupsX
    }
    BVHIndirectArgs[1] = 1;
    BVHIndirectArgs[2] = 1;

    // Переносим "next" в "current" для следующей итерации BVH_Main и
    // обнуляем "next" для накопления в предстоящей итерации.
    GlobalCounters.Store(8, nextActiveCount); // current ActiveCount на следующий шаг
    GlobalCounters.Store(4, 0);               // next-счётчик обнулён под новую итерацию
}

#pragma endregion