using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PhysXCompute.Logic.VulcanLogic.Initialization
{
    public class MortonPair_logic
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MortonPair
        {
            public uint MortonCode;
            public uint ObjectID;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SceneBounds
        {
            public float SceneMinX, SceneMinY, SceneMinZ; // float3
            public float SceneInvSizeX, SceneInvSizeY, SceneInvSizeZ; // float3
            public uint ObjectCount;
        }

        public void Test()
        {
            using var mortonShader = new MortonInstance(vulkan, buffersLog, pipeline, layout);

            int objectsCount = 1024;

            // Выделяем буферы на основе точного размера структур в байтах и их типов
            mortonShader.AllocateBuffers(
                (sizeof(SceneBounds), DescriptorType.UniformBuffer), // b0: Constant Buffer
                (objectsCount * sizeof(MortonPair), DescriptorType.StorageBuffer) // u0: RWStructuredBuffer
            );

            // Создаем данные сцены и отправляем в Constant Buffer (Binding 0)
            SceneBounds boundsData = new SceneBounds
            {
                SceneMinX = 0,
                SceneMinY = 0,
                SceneMinZ = 0,
                SceneInvSizeX = 0.01f,
                SceneInvSizeY = 0.01f,
                SceneInvSizeZ = 0.01f,
                ObjectCount = (uint)objectsCount
            };
            mortonShader.WriteBinding0(new[] { boundsData });

            // Вызываем расчет на GPU (1024 объекта / 64 потока = 16 групп)
            mortonShader.Dispatch(groupCountX: 16);

            // Забираем результаты расчета кодов Мортона из GPU в C# массив одной строчкой!
            MortonPair[] generatedPairs = mortonShader.ReadBinding1<MortonPair>(objectsCount);
        }
    }
}
