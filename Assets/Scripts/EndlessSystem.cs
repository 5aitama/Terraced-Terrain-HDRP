using UnityEngine;
using UnityEngine.Rendering;

using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

using Saitama.Mathematics;
using Saitama.ProceduralMesh;

public struct TerracedTileTag : IComponentData { }
public struct TerracedTileNeedBuildTag : IComponentData { }
public struct TerracedTileNeedUpdateMesh : IComponentData { }

public class EndlessSystem : ComponentSystem
{
    private NativeHashMap<int3, Entity> tilesA;
    private NativeHashMap<int3, Entity> tilesB;

    private Camera mainCam;
    private Material terracedTileMaterial;

    public int2 terrainAmount = 8;
    public int2 tileAmount = 16;

    protected override void OnCreate()
    {
        GraphicsSettings.useScriptableRenderPipelineBatching = true;

        // Get the main camera
        mainCam = GameObject.FindObjectOfType<Camera>();
        
        // Load the material
        terracedTileMaterial = Resources.Load<Material>("TerracedTerrainMaterial");

        // The two hashmap that contains tiles entity by their position.
        tilesA = new NativeHashMap<int3, Entity>(4, Allocator.Persistent);
        tilesB = new NativeHashMap<int3, Entity>(4, Allocator.Persistent);

        var t = terrainAmount.x * terrainAmount.y;
        var c = terrainAmount / 2;

        // Create terraced tiles
        for(var i = 0; i < t; i++)
        {
            var pos2D = (i.To2D(terrainAmount) - c) * tileAmount;
            var pos3D = new int3(pos2D.x, 0, pos2D.y);

            tilesA.Add(pos3D, CreateTerracedTile(pos3D));
        }
    }

    protected override void OnUpdate()
    {
        var cameraPos = ((float3)mainCam.transform.position).xz;

        var tAmount = terrainAmount / 2 * tileAmount;
        var cameraPosTile = (int2)math.round(cameraPos / tAmount);

        cameraPosTile *= terrainAmount / 2 * tileAmount;

        // Debug.DrawLine(mainCam.transform.position, new float3(cameraPosTile.x, 0, cameraPosTile.y));

        //Debug.Log(math.round(cameraPos / tAmount));
        var t = terrainAmount.x * terrainAmount.y;
        var c = terrainAmount / 2;

        for(var i = 0; i < t; i++)
        {
            var pos2D = (i.To2D(terrainAmount) - c) * tileAmount + cameraPosTile;
            var pos3D = (new int3(pos2D.x, 0, pos2D.y));

            if(tilesA.ContainsKey(pos3D))
            {
                tilesB.Add(pos3D, tilesA[pos3D]);
                tilesA.Remove(pos3D);
            }
            else
            {
                // Create new tile
                tilesB.Add(pos3D, CreateTerracedTile(pos3D));
            }
        }

        var keys = tilesA.GetKeyArray(Allocator.Temp);

        // Destroy tiles...
        for(var i = 0; i < keys.Length; i++)
        {
            EntityManager.DestroyEntity(tilesA[keys[i]]);
            tilesA.Remove(keys[i]);
        }

        var keyValueArray = tilesB.GetKeyValueArrays(Allocator.Temp);

        // Swap hash map
        for(var i = 0; i < keyValueArray.Keys.Length; i++)
        {
            tilesA.Add(keyValueArray.Keys[i], keyValueArray.Values[i]);
            tilesB.Remove(keyValueArray.Keys[i]);
        }
    }

    protected override void OnDestroy()
    {
        tilesA.Dispose();
        tilesB.Dispose();
    }

    protected Entity CreateTerracedTile(int3 position)
    {
        var e = EntityManager.CreateEntity();

        EntityManager.AddComponentData(e, new Translation { Value = position });
        EntityManager.AddComponentData(e, new Rotation { Value = quaternion.identity });

        EntityManager.AddComponent<TerracedTileTag>(e);
        EntityManager.AddComponent<TerracedTileNeedBuildTag>(e);

        EntityManager.AddComponent<LocalToWorld>(e);
        EntityManager.AddComponent<RenderBounds>(e);

        EntityManager.AddBuffer<Triangle>(e);
        EntityManager.AddBuffer<Vertex>(e);

        EntityManager.AddSharedComponentData(e, new RenderMesh 
        {
            mesh                 = new Mesh(),
            material             = terracedTileMaterial,
            subMesh              = 0,
            layer                = 0,
            castShadows          = ShadowCastingMode.On,
            receiveShadows       = true,
            needMotionVectorPass = true,
        });

        return e;
    }
}
