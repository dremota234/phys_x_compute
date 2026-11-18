#pragma region BVH Traversal

struct CandidatePair
{
    uint objA; // globalNodeIdx (== objBufferIndex для листьев 0..N-1)
    uint objB;
};

StructuredBuffer<BVHNode>         In_BVHNodes           : register(t0);
RWStructuredBuffer<CandidatePair> Out_CandidatePairs    : register(u0); // буфер с парами  под gjk 
RWStructuredBuffer<uint>          Out_PairCount         : register(u1); // [0] = атомарный счётчик

cbuffer BVHTraversalSettings : register(b0)
{
    uint ObjectCount_Trav;   // N — количество листьев == количество объектов
    uint MaxPairsCapacity;   // размер Out_CandidatePairs, чтобы не улететь за границу
};

StructuredBuffer<uint> In_RootNodeIndex : register(t1);

bool OverlapAABB(AABB a, AABB b)
{
    return (a.minPos.x <= b.maxPos.x && a.maxPos.x >= b.minPos.x) &&
           (a.minPos.y <= b.maxPos.y && a.maxPos.y >= b.minPos.y) &&
           (a.minPos.z <= b.maxPos.z && a.maxPos.z >= b.minPos.z);
}

void EmitPair(uint idA, uint idB)
{
    uint writeIdx;
    InterlockedAdd(Out_PairCount[0], 1, writeIdx);
    if (writeIdx < MaxPairsCapacity)
    {
        CandidatePair p;
        p.objA = idA;
        p.objB = idB;
        Out_CandidatePairs[writeIdx] = p;
    }
}

#define STACK_SIZE 64

[numthreads(64, 1, 1)]
void CS_TraverseBVH(uint3 DTid : SV_DispatchThreadID)
{
    uint myLeafIdx = DTid.x;
    
    if (myLeafIdx >= ObjectCount_Trav) return;
    
    BVHNode node = In_BVHNodes[myLeafIdx];
    AABB myBox = node.bounds;
    uint myObjId = node.leftChild; // у листа leftChild переиспользован под objectId

    // Локальный стек индексов нод, которые предстоит посетить
    uint stack[STACK_SIZE];
    int stackPtr = 0;
    stack[stackPtr++] = In_RootNodeIndex[0];

    while (stackPtr > 0)
    {
        uint nodeIdx = stack[--stackPtr];
        BVHNode node0 = In_BVHNodes[nodeIdx];

        // Грубая отсечка: если AABB ноды (поддерева) не пересекает нас — пропускаем
        if (!OverlapAABB(myBox, node0.bounds)) continue;
        
        if (node0.isLeaf != 0)
        {
            // У листа leftChild переиспользован под objectId (см. BVH_Main).
            uint otherObjId = node0.leftChild;

            if (otherObjId != myObjId && myObjId < otherObjId)
            {
                EmitPair(myObjId, otherObjId);
            }
            continue;
        }
        
        if (stackPtr < STACK_SIZE) stack[stackPtr++] = node0.leftChild;
        if (stackPtr < STACK_SIZE) stack[stackPtr++] = node0.rightChild;
    }
}

#pragma endregion