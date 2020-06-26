using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Collections;

using Saitama.Mathematics;
using Saitama.ProceduralMesh;

using Terraced;
using Terraced.Shapes;

public class TerracedTileMeshUpdateSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        Entities.WithAllReadOnly<TerracedTileNeedUpdateMesh>()
                .WithNone<TerracedTileNeedBuildTag>()
                .ForEach((Entity e, RenderMesh renderMesh, DynamicBuffer<Vertex> vBuff, DynamicBuffer<Triangle> tBuff) =>
                {
                    renderMesh.mesh.Update(tBuff.AsNativeArray(), vBuff.AsNativeArray());

                    EntityManager.SetComponentData<RenderBounds>(e, new RenderBounds
                    {
                        Value = new AABB
                        {
                            Center = renderMesh.mesh.bounds.center,
                            Extents = renderMesh.mesh.bounds.extents,
                        }
                    });
                }
        );
    }
}

public class TerracedTileBuilderSystem : JobComponentSystem
{
    protected EndlessSystem m_EndlessSystem;
    private EntityQuery m_Query;
    private EndSimulationEntityCommandBufferSystem m_EndSimulationEcbSystem;
    private EntityCommandBuffer m_Ecb;

    [BurstCompile]
    private struct GenerateTerracedGrid : IJobParallelFor
    {
        [ReadOnly]
        public int2 tileAmount;

        [WriteOnly]
        public NativeArray<Square> squares;

        [ReadOnly]
        public float noiseFrequency;
        
        [ReadOnly]
        public float noiseAmplitude;
        
        [ReadOnly]
        public float3 noiseOffset;

        public void Execute(int index)
        {
            var pos2D = index.To2D(tileAmount);
            var pos3D = new float3(pos2D.x, 0, pos2D.y);

            var square = new Square(
                pos3D, 
                pos3D + new float3(0, 0, 1), 
                pos3D + new float3(1, 0, 1), 
                pos3D + new float3(1, 0, 0)
            );

            square.p0.y = (1 + noise.snoise((square[0] + noiseOffset) * noiseFrequency))  * .5f * noiseAmplitude;
            square.p1.y = (1 + noise.snoise((square[1] + noiseOffset) * noiseFrequency))  * .5f * noiseAmplitude;
            square.p2.y = (1 + noise.snoise((square[2] + noiseOffset) * noiseFrequency))  * .5f * noiseAmplitude;
            square.p3.y = (1 + noise.snoise((square[3] + noiseOffset) * noiseFrequency))  * .5f * noiseAmplitude;

            squares[index] = square;
        }
    }

    [BurstCompile]
    private struct BuildTerracedTilesJob : IJob
    {
        [ReadOnly]
        public NativeArray<Square> Squares;

        [WriteOnly]
        public NativeList<Triangle> Triangles;

        public NativeList<Vertex> Vertices;

        public void Execute()
        {
            for(var i = 0; i < Squares.Length; i++)
                TerracedTile.BuildFrom(Squares[i], ref Vertices, ref Triangles);
        }
    }

    
    private struct MarkNeedUpdateMeshJob : IJob
    {
        [ReadOnly]
        public Entity entity;

        [ReadOnly]
        public int index;

        public EntityCommandBuffer ecb;

        [ReadOnly]
        public NativeArray<Vertex> Vertices;

        [ReadOnly]
        public NativeArray<Triangle> Triangles;

        public void Execute()
        {
            // Add vertex array to vertex buffer
            ecb.SetBuffer<Vertex>(entity).CopyFrom(Vertices);

            // Add triangle array to triangle buffer
            ecb.SetBuffer<Triangle>(entity).CopyFrom(Triangles);

            ecb.AddComponent<TerracedTileNeedUpdateMesh>(entity);
        }
        
    }

    protected override void OnCreate()
    {
        m_EndSimulationEcbSystem = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
        m_EndlessSystem = World.GetOrCreateSystem<EndlessSystem>();
    }

    protected override JobHandle OnUpdate(JobHandle inputDeps)
    {
        var tileAmount = m_EndlessSystem.tileAmount;
        var totalTileAmount = tileAmount.x * tileAmount.y;

        m_Query = GetEntityQuery(ComponentType.ReadOnly<Translation>(),
                                 ComponentType.ReadOnly<TerracedTileTag>(), 
                                 ComponentType.ReadOnly<TerracedTileNeedBuildTag>());

        var entities = m_Query.ToEntityArray(Allocator.TempJob);
        var translations = m_Query.ToComponentDataArray<Translation>(Allocator.TempJob);
        
        var jobs = new NativeList<JobHandle>(Allocator.Temp);

        for(var i = 0; i < entities.Length; i++)
        {
            var m_Ecb = m_EndSimulationEcbSystem.CreateCommandBuffer();
            var vertices    = new NativeList<Vertex>(Allocator.TempJob);
            var triangles   = new NativeList<Triangle>(Allocator.TempJob);

            var squares     = new NativeArray<Square>(totalTileAmount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var j0 = new GenerateTerracedGrid
            {
                tileAmount      = tileAmount,
                noiseFrequency  = 0.05f,
                noiseAmplitude  = 8f,
                noiseOffset     = translations[i].Value,
                squares         = squares,
            }
            .Schedule(totalTileAmount, 32);

            // Build generated tiles from generated squares
            var j1 = new BuildTerracedTilesJob
            {
                Squares     = squares,
                Vertices    = vertices,
                Triangles   = triangles,
            }
            .Schedule(j0);

            // Smooth normal of all vertex inside "vertices"
            var j2 = new NormalSolver.RecalculateNormalsJob
            {
                angle = 60,
                tris  = triangles.AsDeferredJobArray(),
                vert  = vertices.AsDeferredJobArray(),
            }.Schedule(j1);

            // Add component that tell another system that it can build from this entity...
            var j3 = new MarkNeedUpdateMeshJob
            {
                entity  = entities[i],
                ecb     = m_Ecb,
                index   = i,

                Vertices    = vertices.AsDeferredJobArray(),
                Triangles   = triangles.AsDeferredJobArray(),
            }
            .Schedule(j2);

            var j4 = squares.Dispose(j3);
            var j5 = triangles.Dispose(j4);
            var j6 = vertices.Dispose(j5);

            jobs.Add(j6);

            m_EndSimulationEcbSystem.AddJobHandleForProducer(j3);
            
            EntityManager.RemoveComponent<TerracedTileNeedBuildTag>(entities[i]);
        }

        var combinedJobs = JobHandle.CombineDependencies(jobs);

        combinedJobs = translations.Dispose(combinedJobs);
        combinedJobs = entities.Dispose(combinedJobs);

        return inputDeps;
    }
}
