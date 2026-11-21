// KumaBeer 2026
// https://assetstore.unity.com/publishers/22509
// Provided as it is for free to use
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace VertexRecolor
{
    public class VertexRecolor : EditorWindow
    {
        private GameObject target;
        private float hue, sat = 1f, val = 1f;
        private Color tintColor = Color.white;
        private bool rewriteAlpha = false;
        private float baseAlpha = 1f;
        private Vector3 alphaSubtract = Vector3.zero;
        private Vector3 alphaDivide = Vector3.one;
        private bool useGradientAsTintMask = false;

        private readonly List<Mesh> sceneMeshes = new();
        private readonly List<Transform> meshTransforms = new(); // Добавлено для хранения трансформов
        private readonly List<Color[]> originalColors = new();
        private readonly List<Bounds> meshBounds = new();
        private readonly List<Color[]> calculatedColors = new();

        [MenuItem("Tools/KumaBeer/Vertex Recolor")]
        private static void Init() => GetWindow<VertexRecolor>("Vertex Recolor");

        private void OnGUI()
        {
            target = (GameObject)EditorGUILayout.ObjectField("Target", target, typeof(GameObject), true);
            if (target && GUILayout.Button("Search for vertex colors")) SearchforVC();
            
            if (sceneMeshes.Count == 0)
            {
                EditorGUILayout.HelpBox("Use object with MeshFilter/LODGroup. SkinnedMesh is not supported.", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();
            hue = EditorGUILayout.Slider("Hue", hue, 0f, 360f);
            sat = EditorGUILayout.Slider("Saturation", sat, 0f, 5f);
            val = EditorGUILayout.Slider("Value", val, 0f, 5f);
            tintColor = EditorGUILayout.ColorField("Tint Color", tintColor);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Alpha Settings", EditorStyles.boldLabel);
            rewriteAlpha = EditorGUILayout.Toggle("Rewrite Alpha", rewriteAlpha);
            if (rewriteAlpha)
            {
                baseAlpha = EditorGUILayout.Slider("Base Alpha", baseAlpha, 0f, 1f);
                alphaSubtract = EditorGUILayout.Vector3Field("Gradient Subtract", alphaSubtract);
                alphaDivide = EditorGUILayout.Vector3Field("Gradient Divide", alphaDivide);
                useGradientAsTintMask = EditorGUILayout.Toggle("Use gradient as tint mask", useGradientAsTintMask);
            }
            if (EditorGUI.EndChangeCheck()) Apply();
            
            if (GUILayout.Button("Export meshes and assemble prefab")) Save();
        }

        private void SearchforVC()
        {
            sceneMeshes.Clear();
            meshTransforms.Clear();
            originalColors.Clear();
            meshBounds.Clear();
            calculatedColors.Clear();
            
            if (!target) return;
            
            var lod = target.GetComponent<LODGroup>();
            var filters = new List<MeshFilter>();
            var transforms = new List<Transform>();

            if (lod)
            {
                foreach (var l in lod.GetLODs())
                    if (l.renderers != null)
                        foreach (var r in l.renderers)
                            if (r && !(r is SkinnedMeshRenderer))
                            {
                                var m = r.GetComponent<MeshFilter>();
                                if (m) 
                                {
                                    filters.Add(m);
                                    transforms.Add(m.transform);
                                }
                            }
            }
            else
            {
                foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                    if (r && !(r is SkinnedMeshRenderer))
                    {
                        var m = r.GetComponent<MeshFilter>();
                        if (m) 
                        {
                            filters.Add(m);
                            transforms.Add(m.transform);
                        }
                    }
            }

            for (int i = 0; i < filters.Count; i++)
            {
                var f = filters[i];
                var tr = transforms[i];
                
                if (f.sharedMesh == null) continue;
                
                var meshInstance = Object.Instantiate(f.sharedMesh);
                meshInstance.name = f.sharedMesh.name;
                f.mesh = meshInstance;
                
                sceneMeshes.Add(meshInstance);
                meshTransforms.Add(tr); // Сохраняем трансформ
                
                var c = meshInstance.colors;
                originalColors.Add((c != null && c.Length > 0) ? c : new Color[meshInstance.vertexCount]);
                meshBounds.Add(meshInstance.bounds);
            }
            
            if (sceneMeshes.Count > 0) Apply();
        }

        // ObjBounds////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        private Bounds GetWorldAABB(Bounds localBounds, Transform tr)
        {
            Vector3[] corners = new Vector3[8];
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            
            corners[0] = tr.TransformPoint(new Vector3(min.x, min.y, min.z));
            corners[1] = tr.TransformPoint(new Vector3(max.x, min.y, min.z));
            corners[2] = tr.TransformPoint(new Vector3(min.x, max.y, min.z));
            corners[3] = tr.TransformPoint(new Vector3(max.x, max.y, min.z));
            corners[4] = tr.TransformPoint(new Vector3(min.x, min.y, max.z));
            corners[5] = tr.TransformPoint(new Vector3(max.x, min.y, max.z));
            corners[6] = tr.TransformPoint(new Vector3(min.x, max.y, max.z));
            corners[7] = tr.TransformPoint(new Vector3(max.x, max.y, max.z));

            Vector3 worldMin = corners[0];
            Vector3 worldMax = corners[0];
            
            for (int i = 1; i < 8; i++)
            {
                worldMin = Vector3.Min(worldMin, corners[i]);
                worldMax = Vector3.Max(worldMax, corners[i]);
            }
            
            return new Bounds((worldMin + worldMax) * 0.5f, worldMax - worldMin);
        }

        private float CalculateAlphaGradient(Vector3 worldPos, Bounds worldBounds, Vector3 subtract, Vector3 divide)
        {
            Vector3 min = worldBounds.min;
            Vector3 size = worldBounds.size;
            Vector3 t = Vector3.zero;
            
            // clamp////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            if (size.x > 0.001f) t.x = (worldPos.x - min.x) / size.x;
            if (size.y > 0.001f) t.y = (worldPos.y - min.y) / size.y;
            if (size.z > 0.001f) t.z = (worldPos.z - min.z) / size.z;
            
            t -= subtract;
            float gradient = 0f;
            float activeAxesCount = 0f;
            
            if (Mathf.Abs(divide.x) > 0.001f) { gradient += t.x / divide.x; activeAxesCount++; }
            if (Mathf.Abs(divide.y) > 0.001f) { gradient += t.y / divide.y; activeAxesCount++; }
            if (Mathf.Abs(divide.z) > 0.001f) { gradient += t.z / divide.z; activeAxesCount++; }
            
            if (activeAxesCount > 0f)
                return Mathf.Clamp01(gradient / activeAxesCount);
                
            return 0f;
        }

        private void Apply()
        {
            calculatedColors.Clear();
            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                var src = originalColors[i];
                var dst = new Color[src.Length];
                var mesh = sceneMeshes[i];
                var tr = meshTransforms[i];
                var vertices = mesh.vertices;
                
                var worldBounds = GetWorldAABB(meshBounds[i], tr);
                
                for (int j = 0; j < src.Length; j++)
                {
                    Color.RGBToHSV(src[j], out float h, out float s, out float v);
                    h = (h * 360f + hue) % 360f / 360f;
                    s = Mathf.Clamp01(s * sat);
                    v = Mathf.Clamp01(v * val);
                    dst[j] = Color.HSVToRGB(Mathf.Clamp01(h), s, v, false);
                    
                    // localtoworld
                    Vector3 worldPos = tr.TransformPoint(vertices[j]);
                    float gradient = CalculateAlphaGradient(worldPos, worldBounds, alphaSubtract, alphaDivide);
                    
                    if (useGradientAsTintMask)
                    {
                        float mask = 1f - gradient;
                        Color effectiveTint = Color.Lerp(Color.white, tintColor, mask);
                        dst[j] *= effectiveTint;
                    }
                    else
                    {
                        dst[j] *= tintColor;
                    }
                    
                    if (rewriteAlpha)
                    {
                        dst[j].a = Mathf.Clamp01(Mathf.Max(gradient, baseAlpha));
                    }
                    else
                    {
                        dst[j].a = src[j].a;
                    }
                }
                calculatedColors.Add(dst);
                mesh.colors = dst;
            }
        }

        private void Save()
        {
            if (!target || calculatedColors.Count == 0)
            {
                EditorUtility.DisplayDialog("Error", "Please click 'Search for vertex colors' first", "OK");
                return;
            }
            string folder = EditorUtility.SaveFolderPanel("Export Recolored Assets", "Assets", "");
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith(Application.dataPath))
            {
                EditorUtility.DisplayDialog("Error", "Please select a valid Assets subfolder", "OK");
                return;
            }
            string relativeFolder = "Assets" + folder.Substring(Application.dataPath.Length);
            relativeFolder = relativeFolder.Replace('\\', '/').TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(relativeFolder))
            {
                string parent = Path.GetDirectoryName(relativeFolder).Replace('\\', '/');
                string name = Path.GetFileName(relativeFolder);
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(name))
                    AssetDatabase.CreateFolder(parent, name);
            }
            List<Mesh> exportedMeshes = new List<Mesh>();
            for (int i = 0; i < sceneMeshes.Count; i++)
            {
                var originalMesh = sceneMeshes[i];
                if (originalMesh == null) continue;
                var newMesh = new Mesh
                {
                    name = originalMesh.name + "_Recolored",
                    vertices = originalMesh.vertices,
                    normals = originalMesh.normals,
                    tangents = originalMesh.tangents,
                    uv = originalMesh.uv,
                    uv2 = originalMesh.uv2,
                    colors = calculatedColors[i]
                };
                if (originalMesh.subMeshCount > 0)
                {
                    for (int s = 0; s < originalMesh.subMeshCount; s++)
                    {
                        var indices = new List<int>();
                        originalMesh.GetIndices(indices, s, true);
                        newMesh.SetIndices(indices, originalMesh.GetTopology(s), s, true);
                    }
                }
                newMesh.RecalculateBounds();
                newMesh.UploadMeshData(true);
                string assetPath = Path.Combine(relativeFolder, newMesh.name + ".asset");
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                AssetDatabase.CreateAsset(newMesh, assetPath);
                exportedMeshes.Add(newMesh);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            GameObject clone = Object.Instantiate(target);
            if (clone == null)
            {
                EditorUtility.DisplayDialog("Error", "Failed to duplicate object for color", "OK");
                return;
            }
            clone.name = target.name + "_Recolored";
            var cloneFilters = clone.GetComponentsInChildren<MeshFilter>(true);
            int meshIdx = 0;
            foreach (var filter in cloneFilters)
            {
                if (meshIdx < exportedMeshes.Count)
                {
                    filter.sharedMesh = exportedMeshes[meshIdx];
                    meshIdx++;
                }
            }

            string prefabPath = Path.Combine(relativeFolder, clone.name + ".prefab");
            prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabPath);
            PrefabUtility.SaveAsPrefabAsset(clone, prefabPath);
            Object.DestroyImmediate(clone);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Success", $"Recolored prefab saved!\n\nMeshes: {exportedMeshes.Count}\nPath: {prefabPath}", "OK");
        }
    }
}