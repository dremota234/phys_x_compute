#pragma region Data

struct AABB 
{
    float3 minPos;
    float3 maxPos;
};
struct Object
{
    AABB bounds;
    uint objId_InGlobalBuffer;
};

struct GlobalObj 
{
    float4x4    transform;
    float3      massCenter;         uint     colliderType;
    float3      minAABB;            uint     objBufferIndex;  
    float3      maxAABB;            uint     additionalBufferIndex;  
    float3      rotation;           float    mass;   
    float3      position;           float    staticFriction;
    float3      velocity;           float    dynamicFriction; 
    float3      angularVelocity;    float    updateMask;
};

RWStructuredBuffer<Object> MovedObjects;
RWStructuredBuffer<uint> MovedObjectCounter; // size 1
RWStructuredBuffer<GlobalObj> GlobalObjects; // список всех обектов

#pragma endregion

void AddMovedObject(uint objId_InGlobalBuffer, AABB bounds)
{
    uint movedObjectId;
    InterlockedAdd(MovedObjectCounter[0], 1, movedObjectId);
    
    Object object;
    object.objId_InGlobalBuffer = objId_InGlobalBuffer;
    object.bounds = bounds;
    
    MovedObjects[movedObjectId] = object;
}