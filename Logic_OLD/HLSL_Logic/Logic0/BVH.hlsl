#pragma region BVH
struct BVHNode {
    AABB   bounds;
    uint   leftChild;
    uint   rightChild;
    uint   parent;
    uint   isLeaf;
};

// использовались оба под u0 одновременно в BVH_MAin - конфликт слота.
RWStructuredBuffer<BVHNode> BVHNodes           : register(u0);  // Дерево
StructuredBuffer  <uint>    In_ActiveIndices   : register(t1);  // Индексы нод на текущем шаге
RWStructuredBuffer<uint>    Out_ActiveIndices  : register(u1);  // Индексы нод для следующего шага

// Буфер со счетчиками (выделение памяти под внутренние ноды и под сборку следующего шага)
// Байт 0 - Глобальный индекс для выделения внутренних нод (стартует с N)
// Байт 4 - Счетчик активных нод для следующего шага (стартует с 0)
RWByteAddressBuffer    GlobalCounters     : register(u2); 

cbuffer PLOCSettings : register(b0) {
    uint ActiveCount;                   // Сколько нод участвует в этой итерации
    uint SearchWindow;                  // Радиус поиска соседей (в оригинале статьи обычно 16 или 32)
    uint ObjectCount_PLOCSettings;      // Общее число N
};

// Функция объединения двух AABB в один общий
AABB MergeAABB(AABB a, AABB b) {
    AABB merged;
    merged.minPos = min(a.minPos, b.minPos);
    merged.maxPos = max(a.maxPos, b.maxPos);
    return merged;
}

// Вычисление площади поверхности AABB (Метрика SAH)
float GetSurfaceArea(AABB box) {
    float3 d = box.maxPos - box.minPos;
    return 2.0f * (d.x * d.y + d.y * d.z + d.z * d.x);
}


#define GROUP_SIZE 64

groupshared uint SharedNodeIndices[GROUP_SIZE];                 // Реальные ID нод в дереве
groupshared uint SharedBestNeighbors[GROUP_SIZE];               // Индексы лучших соседей, которых выбрал каждый поток

[numthreads(64, 1, 1)]
void BVH_Main(uint3 dtID : SV_DispatchThreadID, uint  localIdx : SV_GroupIndex)
{
    uint currentActiveIdx = dtID.x;
    if (currentActiveIdx >= ActiveCount) return;

    // 1. Получаем индекс текущей ноды в дереве
    uint myNodeIdx = In_ActiveIndices[currentActiveIdx];

    if (myNodeIdx < ObjectCount_PLOCSettings)
    {
        BVHNodes[myNodeIdx].isLeaf     = 1;
        BVHNodes[myNodeIdx].leftChild  = myNodeIdx;      // objectId листа
        BVHNodes[myNodeIdx].rightChild = 0xFFFFFFFFu;
        BVHNodes[myNodeIdx].parent     = 0xFFFFFFFFu;
        BVHNodes[myNodeIdx].bounds     = MovedObjects[myNodeIdx].bounds; // читаем ТОЛЬКО когда точно лист
    }

    AABB myBox = BVHNodes[myNodeIdx].bounds;
    
    // 2. ПОИСК БЛИЖАЙШЕГО СОСЕДА (Nearest Neighbor)
    uint bestNeighborActiveIdx = 0xFFFFFFFFu;
    float minArea = 3.402823466e+38f; // FLT_MAX

    // Сканируем окно соседей вокруг текущего индекса в активном массиве
    int start = max(0, (int)currentActiveIdx - (int)SearchWindow);
    int end   = min((int)ActiveCount - 1, (int)currentActiveIdx + (int)SearchWindow);

    for (int j = start; j <= end; j++)
    {
        if ((uint)j == currentActiveIdx) continue;

        uint neighborNodeIdx = In_ActiveIndices[j];
        AABB neighborBox = BVHNodes[neighborNodeIdx].bounds;

        // Считаем площадь объединенного бокса
        AABB combinedBox = MergeAABB(myBox, neighborBox);
        float area = GetSurfaceArea(combinedBox);

        if (area < minArea) {
            minArea = area;
            bestNeighborActiveIdx = (uint)j;
        }
    }

    // 3. ВЗАИМНОЕ СЛИЯНИЕ (Mutual Nearest Neighbor)
    SharedNodeIndices[localIdx]   = myNodeIdx;
    SharedBestNeighbors[localIdx] = bestNeighborActiveIdx;
    
    GroupMemoryBarrierWithGroupSync();  // СИНХРОНИЗАЦИЯ: Ждем, пока вся группа (64 потоков) запишет свои выборы
    
    bool isMutualMatch = false;
    uint neighborNodeIdx = 0xFFFFFFFFu;
    
    uint groupStartActiveIdx = currentActiveIdx - localIdx;
    int neighborLocalIdx = (int)bestNeighborActiveIdx - (int)groupStartActiveIdx;

    //  Если наш сосед находится внутри нашей же группы потоков
    if (neighborLocalIdx >= 0 && neighborLocalIdx < GROUP_SIZE)
    {
        // Читаем из быстрой LDS-памяти, кого выбрал этот сосед
        uint neighborsChoice = SharedBestNeighbors[neighborLocalIdx];
        
        // Взаимность подтверждена, если сосед выбрал НАС (наш глобальный индекс)
        if (neighborsChoice == currentActiveIdx)
        {
            isMutualMatch = true;
            neighborNodeIdx = SharedNodeIndices[neighborLocalIdx];
        }
    }
    
    // Если сосед оказался за пределами группы (на стыке блоков)    :(
    else if (bestNeighborActiveIdx < ActiveCount)
    {
        // 1. Достаем коробку нашего пограничного соседа напрямую из дерева
        uint globalNeighborNodeIdx = In_ActiveIndices[bestNeighborActiveIdx];
        AABB neighborBox = BVHNodes[globalNeighborNodeIdx].bounds;

        // 2. Нам нужно проверить: выберет ли этот сосед НАС взаимно?
        // Для этого мы встаем на место соседа и сканируем ЕГО окно поиска.
        uint neighborsBestChoice = 0xFFFFFFFFu;
        float minAreaForNeighbor = 3.402823466e+38f; // FLT_MAX

        int nStart = max(0, (int)bestNeighborActiveIdx - (int)SearchWindow);
        int nEnd   = min((int)ActiveCount - 1, (int)bestNeighborActiveIdx + (int)SearchWindow);

        for (int k = nStart; k <= nEnd; k++) 
        {
            if ((uint)k == bestNeighborActiveIdx) continue;

            uint candidateNodeIdx = In_ActiveIndices[k];
            AABB candidateBox = BVHNodes[candidateNodeIdx].bounds;

            // Считаем площадь объединения соседа с кандидатом 'k'
            AABB combinedBox = MergeAABB(neighborBox, candidateBox);
            float area = GetSurfaceArea(combinedBox);

            if (area < minAreaForNeighbor) 
            {
                minAreaForNeighbor = area;
                neighborsBestChoice = (uint)k;
            }
        }

        // 3. Если сосед, перебрав свое окно, выбрал именно НАШ индекс
        if (neighborsBestChoice == currentActiveIdx)
        {
            isMutualMatch = true;                       // Взаимность доказана
            neighborNodeIdx = globalNeighborNodeIdx;    // Запоминаем ID его ноды в дереве
        }
    }
    
    if (isMutualMatch)
    {
        // Взаимная пара найдена: только поток с меньшим activeIdx физически
        // создаёт родителя (чтобы не сделать это дважды для одной пары).
        if (currentActiveIdx < bestNeighborActiveIdx)
        {
            // 1. Атомарно выделяем место под новую внутреннюю ноду в дереве
            uint newParentNodeIdx;
            GlobalCounters.InterlockedAdd(0, 1, newParentNodeIdx); // Счетчик внутренних нод (начиная с N)

            // 2. Объединяем коробки детей
            uint nodeA = myNodeIdx;
            uint nodeB = neighborNodeIdx;
            
            BVHNode parentNode;
            parentNode.bounds     = MergeAABB(BVHNodes[nodeA].bounds, BVHNodes[nodeB].bounds);
            parentNode.leftChild  = nodeA;
            parentNode.rightChild = nodeB;
            parentNode.parent     = 0xFFFFFFFFu;        // Родителя у этой новой ноды пока нет
            parentNode.isLeaf     = 0;                  // Это внутренняя нода
            
            BVHNodes[newParentNodeIdx] = parentNode;    // Записываем родителя в дерево

            // 3. Прописываем детям индекс их нового родителя
            BVHNodes[nodeA].parent = newParentNodeIdx;
            BVHNodes[nodeB].parent = newParentNodeIdx;

            // 4. Добавляем НОВУЮ ноду в список активных для СЛЕДУЮЩЕЙ итерации
            uint nextActivePos;
            GlobalCounters.InterlockedAdd(4, 1, nextActivePos); // Счетчик активных нод следующего шага
            Out_ActiveIndices[nextActivePos] = newParentNodeIdx;
        }
        // else: "проигравшая" сторона пары ничего не делает - родитель уже
        // добавлен другим потоком, самой ноде в следующий список попадать не нужно.
    }
    else
    {
        uint nextActivePos;
        GlobalCounters.InterlockedAdd(4, 1, nextActivePos);
        Out_ActiveIndices[nextActivePos] = myNodeIdx;
    }
}
#pragma endregion