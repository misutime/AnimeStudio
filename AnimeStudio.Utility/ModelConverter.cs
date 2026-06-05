using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public enum TextureExportMode
    {
        Raw,
        Png,
        Reference
    }

    public class ModelConverter : IImported
    {
        public ImportedFrame RootFrame { get; protected set; }
        public List<ImportedMesh> MeshList { get; protected set; } = new List<ImportedMesh>();
        public List<ImportedMaterial> MaterialList { get; protected set; } = new List<ImportedMaterial>();
        public List<ImportedTexture> TextureList { get; protected set; } = new List<ImportedTexture>();
        public List<ImportedKeyframedAnimation> AnimationList { get; protected set; } = new List<ImportedKeyframedAnimation>();
        public List<ImportedMorph> MorphList { get; protected set; } = new List<ImportedMorph>();

        private Options options;
        private Avatar avatar;
        private HashSet<AnimationClip> animationClipHashSet = new HashSet<AnimationClip>();
        private Dictionary<AnimationClip, string> boundAnimationPathDic = new Dictionary<AnimationClip, string>();
        private Dictionary<uint, string> bonePathHash = new Dictionary<uint, string>();
        private Dictionary<AnimationClip, Dictionary<uint, string>> animationTosCache = new Dictionary<AnimationClip, Dictionary<uint, string>>();
        private Dictionary<Texture2D, string> textureNameDictionary = new Dictionary<Texture2D, string>();
        private Dictionary<Transform, ImportedFrame> transformDictionary = new Dictionary<Transform, ImportedFrame>();
        private bool hasExplicitAnimationList;
        Dictionary<uint, string> morphChannelNames = new Dictionary<uint, string>();

        public ModelConverter(GameObject m_GameObject, Options options, AnimationClip[] animationList = null)
        {
            this.options = options;
            hasExplicitAnimationList = animationList != null;

            if (m_GameObject.m_Animator != null && options.useAnimatorHierarchy)
            {
                InitWithAnimator(m_GameObject.m_Animator);
                if (animationList == null && this.options.collectAnimations)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }
            }
            else
            {
                InitWithGameObject(m_GameObject);
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
        }

        public ModelConverter(string rootName, List<GameObject> m_GameObjects, Options options, AnimationClip[] animationList = null)
        {
            this.options = options;
            hasExplicitAnimationList = animationList != null;

            RootFrame = CreateFrame(rootName, Vector3.Zero, new Quaternion(0, 0, 0, 0), Vector3.One);
            foreach (var m_GameObject in m_GameObjects)
            {
                if (m_GameObject.m_Animator != null && animationList == null && this.options.collectAnimations)
                {
                    CollectAnimationClip(m_GameObject.m_Animator);
                }

                var m_Transform = m_GameObject.m_Transform;
                ConvertTransforms(m_Transform, RootFrame);
                CreateBonePathHash(m_Transform);
            }
            foreach (var m_GameObject in m_GameObjects)
            {
                var m_Transform = m_GameObject.m_Transform;
                ConvertMeshRenderer(m_Transform);
            }
            if (animationList != null)
            {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            ConvertAnimations();
        }

        public ModelConverter(Animator m_Animator, Options options, AnimationClip[] animationList = null)
        {
            this.options = options;
            hasExplicitAnimationList = animationList != null;

            InitWithAnimator(m_Animator);
            if (animationList == null && this.options.collectAnimations)
            {
                CollectAnimationClip(m_Animator);
            }
            else
            {
                if (animationList != null)
                {
                foreach (var animationClip in animationList)
                {
                    animationClipHashSet.Add(animationClip);
                }
            }
            }
            ConvertAnimations();
        }

        private void InitWithAnimator(Animator m_Animator)
        {
            if (m_Animator.m_Avatar.TryGet(out var m_Avatar))
                avatar = m_Avatar;

            m_Animator.m_GameObject.TryGet(out var m_GameObject);
            InitWithGameObject(m_GameObject, m_Animator.m_HasTransformHierarchy);
        }

        private void InitWithGameObject(GameObject m_GameObject, bool hasTransformHierarchy = true)
        {
            var m_Transform = m_GameObject.m_Transform;
            if (!hasTransformHierarchy)
            {
                ConvertTransforms(m_Transform, null);
                DeoptimizeTransformHierarchy();
            }
            else
            {
                var frameList = new List<ImportedFrame>();
                var tempTransform = m_Transform;
                while (tempTransform.m_Father.TryGet(out var m_Father))
                {
                    frameList.Add(ConvertTransform(m_Father));
                    tempTransform = m_Father;
                }
                if (frameList.Count > 0)
                {
                    RootFrame = frameList[frameList.Count - 1];
                    for (var i = frameList.Count - 2; i >= 0; i--)
                    {
                        var frame = frameList[i];
                        var parent = frameList[i + 1];
                        parent.AddChild(frame);
                    }
                    ConvertTransforms(m_Transform, frameList[0]);
                }
                else
                {
                    ConvertTransforms(m_Transform, null);
                }

                CreateBonePathHash(m_Transform);
            }

            ConvertMeshRenderer(m_Transform);
        }

        private void ConvertMeshRenderer(Transform m_Transform)
        {
            m_Transform.m_GameObject.TryGet(out var m_GameObject);

            if (m_GameObject.m_MeshRenderer != null)
            {
                ConvertMeshRenderer(m_GameObject.m_MeshRenderer);
            }

            if (m_GameObject.m_SkinnedMeshRenderer != null)
            {
                ConvertMeshRenderer(m_GameObject.m_SkinnedMeshRenderer);
            }

            if (options.exportAnimations && !hasExplicitAnimationList && m_GameObject.m_Animation != null)
            {
                foreach (var animation in m_GameObject.m_Animation.m_Animations)
                {
                    if (animation.TryGet(out var animationClip))
                    {
                        if (!boundAnimationPathDic.ContainsKey(animationClip))
                        {
                            boundAnimationPathDic.Add(animationClip, GetTransformPath(m_Transform));
                        }
                        animationClipHashSet.Add(animationClip);
                    }
                }
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                if (pptr.TryGet(out var child))
                    ConvertMeshRenderer(child);
            }
        }

        private void CollectAnimationClip(Animator m_Animator)
        {
            if (m_Animator.m_Controller.TryGet(out var m_Controller))
            {
                switch (m_Controller)
                {
                    case AnimatorOverrideController m_AnimatorOverrideController:
                        {
                            if (m_AnimatorOverrideController.m_Controller.TryGet<AnimatorController>(out var m_AnimatorController))
                            {
                                foreach (var pptr in m_AnimatorController.m_AnimationClips)
                                {
                                    if (pptr.TryGet(out var m_AnimationClip))
                                    {
                                        animationClipHashSet.Add(m_AnimationClip);
                                    }
                                }
                            }
                            break;
                        }

                    case AnimatorController m_AnimatorController:
                        {
                            foreach (var pptr in m_AnimatorController.m_AnimationClips)
                            {
                                if (pptr.TryGet(out var m_AnimationClip))
                                {
                                    animationClipHashSet.Add(m_AnimationClip);
                                }
                            }
                            break;
                        }
                }
            }
        }

        private ImportedFrame ConvertTransform(Transform trans)
        {
            var frame = new ImportedFrame(trans.m_Children.Count);
            transformDictionary.Add(trans, frame);
            trans.m_GameObject.TryGet(out var m_GameObject);
            frame.Name = m_GameObject.m_Name;
            SetFrame(frame, trans.m_LocalPosition, trans.m_LocalRotation, trans.m_LocalScale);
            return frame;
        }

        private static ImportedFrame CreateFrame(string name, Vector3 t, Quaternion q, Vector3 s)
        {
            var frame = new ImportedFrame();
            frame.Name = name;
            SetFrame(frame, t, q, s);
            return frame;
        }

        private static void SetFrame(ImportedFrame frame, Vector3 t, Quaternion q, Vector3 s)
        {
            frame.LocalPosition = new Vector3(-t.X, t.Y, t.Z);
            frame.LocalRotation = new Quaternion(q.X, -q.Y, -q.Z, q.W);
            frame.LocalScale = s;
        }

        private void ConvertTransforms(Transform trans, ImportedFrame parent)
        {
            var frame = ConvertTransform(trans);
            if (parent == null)
            {
                RootFrame = frame;
            }
            else
            {
                parent.AddChild(frame);
            }
            foreach (var pptr in trans.m_Children)
            {
                if (pptr.TryGet(out var child))
                    ConvertTransforms(child, frame);
            }
        }

        private void ConvertMeshRenderer(Renderer meshR)
        {
            var mesh = GetMesh(meshR);
            if (mesh == null)
                return;
            meshR.m_GameObject.TryGet(out var m_GameObject2);
            using var meshProfile = Measure("model_mesh", new Dictionary<string, object>
            {
                ["mesh"] = mesh.m_Name,
                ["renderer"] = m_GameObject2?.m_Name,
                ["source"] = mesh.assetsFile?.fullName,
                ["pathId"] = mesh.m_PathID,
                ["vertexCount"] = mesh.m_VertexCount,
                ["submeshCount"] = mesh.m_SubMeshes?.Count ?? 0,
            });
            var iMesh = new ImportedMesh();
            iMesh.Path = GetTransformPath(m_GameObject2.m_Transform);
            iMesh.SubmeshList = new List<ImportedSubmesh>();
            var subHashSet = new HashSet<int>();
            var combine = false;
            int firstSubMesh = 0;
            if (meshR.m_StaticBatchInfo?.subMeshCount > 0)
            {
                firstSubMesh = meshR.m_StaticBatchInfo.firstSubMesh;
                var finalSubMesh = meshR.m_StaticBatchInfo.firstSubMesh + meshR.m_StaticBatchInfo.subMeshCount;
                for (int i = meshR.m_StaticBatchInfo.firstSubMesh; i < finalSubMesh; i++)
                {
                    subHashSet.Add(i);
                }
                combine = true;
            }
            else if (meshR.m_SubsetIndices?.Length > 0)
            {
                firstSubMesh = (int)meshR.m_SubsetIndices.Min(x => x);
                foreach (var index in meshR.m_SubsetIndices)
                {
                    subHashSet.Add((int)index);
                }
                combine = true;
            }

            iMesh.hasNormal = mesh.m_Normals?.Length > 0;
            iMesh.hasUV = new bool[8];
            iMesh.uvType = new int[8];
            for (int uv = 0; uv < 8; uv++)
            {
                var key = $"UV{uv}";
                iMesh.hasUV[uv] = mesh.GetUV(uv)?.Length > 0 && options.uvs[key].Item1;
                iMesh.uvType[uv] = options.uvs[key].Item2;
            }
            iMesh.hasTangent = mesh.m_Tangents != null && mesh.m_Tangents.Length == mesh.m_VertexCount * 4;
            iMesh.hasColor = mesh.m_Colors?.Length > 0;

            int firstFace = 0;
            for (int i = 0; i < mesh.m_SubMeshes.Count; i++)
            {
                int numFaces = (int)mesh.m_SubMeshes[i].indexCount / 3;
                if (subHashSet.Count > 0 && !subHashSet.Contains(i))
                {
                    firstFace += numFaces;
                    continue;
                }
                var submesh = mesh.m_SubMeshes[i];
                var iSubmesh = new ImportedSubmesh();
                Material mat = null;
                if (i - firstSubMesh < meshR.m_Materials.Count)
                {
                    var materialPtr = meshR.m_Materials[i - firstSubMesh];
                    if (materialPtr.TryGet(out var m_Material))
                    {
                        mat = m_Material;
                    }
                    else if (!materialPtr.IsNull)
                    {
                        Logger.Warning($"Unable to resolve material for {m_GameObject2.m_Name} submesh {i}.");
                    }
                }
                ImportedMaterial iMat = ConvertMaterial(mat);
                iSubmesh.Material = iMat.Name;
                iSubmesh.BaseVertex = (int)mesh.m_SubMeshes[i].firstVertex;

                //Face
                iSubmesh.FaceList = new List<ImportedFace>(numFaces);
                var end = firstFace + numFaces;
                for (int f = firstFace; f < end; f++)
                {
                    var face = new ImportedFace();
                    face.VertexIndices = new int[3];
                    face.VertexIndices[0] = (int)(mesh.m_Indices[f * 3 + 2] - submesh.firstVertex);
                    face.VertexIndices[1] = (int)(mesh.m_Indices[f * 3 + 1] - submesh.firstVertex);
                    face.VertexIndices[2] = (int)(mesh.m_Indices[f * 3] - submesh.firstVertex);
                    iSubmesh.FaceList.Add(face);
                }
                firstFace = end;

                iMesh.SubmeshList.Add(iSubmesh);
            }

            var meshVertexCacheKey = GetMeshVertexCacheKey(mesh, iMesh);
            if (
                options.meshVertexCache != null
                && options.meshVertexCache.TryGetValue(meshVertexCacheKey, out var cachedVertexList)
            )
            {
                using (Measure("model_mesh_cache_hit", new Dictionary<string, object>
                {
                    ["mesh"] = mesh.m_Name,
                    ["source"] = mesh.assetsFile?.fullName,
                    ["pathId"] = mesh.m_PathID,
                    ["vertexCount"] = mesh.m_VertexCount,
                }))
                {
                    iMesh.VertexList = CloneVertexList(cachedVertexList);
                }
            }
            else
            {
                // Shared vertex list
                iMesh.VertexList = new List<ImportedVertex>((int)mesh.m_VertexCount);
                for (var j = 0; j < mesh.m_VertexCount; j++)
                {
                    var iVertex = new ImportedVertex();
                    //Vertices
                    int c = 3;
                    if (mesh.m_Vertices.Length == mesh.m_VertexCount * 4)
                    {
                        c = 4;
                    }
                    iVertex.Vertex = new Vector3(-mesh.m_Vertices[j * c], mesh.m_Vertices[j * c + 1], mesh.m_Vertices[j * c + 2]);
                    //Normals
                    if (iMesh.hasNormal)
                    {
                        if (mesh.m_Normals.Length == mesh.m_VertexCount * 3)
                        {
                            c = 3;
                        }
                        else if (mesh.m_Normals.Length == mesh.m_VertexCount * 4)
                        {
                            c = 4;
                        }
                        iVertex.Normal = new Vector3(-mesh.m_Normals[j * c], mesh.m_Normals[j * c + 1], mesh.m_Normals[j * c + 2]);
                    }
                    //UV
                    iVertex.UV = new float[8][];
                    for (int uv = 0; uv < 8; uv++)
                    {
                        if (iMesh.hasUV[uv])
                        {
                            c = 4;
                            var m_UV = mesh.GetUV(uv);
                            if (m_UV.Length == mesh.m_VertexCount * 2)
                            {
                                c = 2;
                            }
                            else if (m_UV.Length == mesh.m_VertexCount * 3)
                            {
                                c = 3;
                            }
                            iVertex.UV[uv] = new[] { m_UV[j * c], m_UV[j * c + 1] };
                        }
                    }
                    //Tangent
                    if (iMesh.hasTangent)
                    {
                        iVertex.Tangent = new Vector4(-mesh.m_Tangents[j * 4], mesh.m_Tangents[j * 4 + 1], mesh.m_Tangents[j * 4 + 2], mesh.m_Tangents[j * 4 + 3]);
                    }
                    //Colors
                    if (iMesh.hasColor)
                    {
                        if (mesh.m_Colors.Length == mesh.m_VertexCount * 3)
                        {
                            iVertex.Color = new Color(mesh.m_Colors[j * 3], mesh.m_Colors[j * 3 + 1], mesh.m_Colors[j * 3 + 2], 1.0f);
                        }
                        else
                        {
                            iVertex.Color = new Color(mesh.m_Colors[j * 4], mesh.m_Colors[j * 4 + 1], mesh.m_Colors[j * 4 + 2], mesh.m_Colors[j * 4 + 3]);
                        }
                    }
                    //BoneInfluence
                    if (mesh.m_Skin?.Count > 0)
                    {
                        var inf = mesh.m_Skin[j];
                        iVertex.BoneIndices = new int[4];
                        iVertex.Weights = new float[4];
                        for (var k = 0; k < 4; k++)
                        {
                            iVertex.BoneIndices[k] = inf.boneIndex[k];
                            iVertex.Weights[k] = inf.weight[k];
                        }
                    }
                    iMesh.VertexList.Add(iVertex);
                }

                if (options.meshVertexCache != null && mesh.m_VertexCount <= options.maxCachedMeshVertices)
                {
                    AddMeshVertexCache(meshVertexCacheKey, iMesh.VertexList);
                }
            }

            if (meshR is SkinnedMeshRenderer sMesh)
            {
                using (Measure("model_skin", new Dictionary<string, object>
                {
                    ["mesh"] = mesh.m_Name,
                    ["renderer"] = m_GameObject2?.m_Name,
                    ["source"] = mesh.assetsFile?.fullName,
                    ["pathId"] = mesh.m_PathID,
                    ["boneCount"] = sMesh.m_Bones?.Count ?? 0,
                    ["bindPoseCount"] = mesh.m_BindPose?.Length ?? 0,
                    ["morphChannelCount"] = mesh.m_Shapes?.channels?.Count ?? 0,
                }))
                {
                    //Bone
                    /*
                     * 0 - None
                     * 1 - m_Bones
                     * 2 - m_BoneNameHashes
                     */
                    var boneType = 0;
                    if (sMesh.m_Bones.Count > 0)
                    {
                        if (sMesh.m_Bones.Count == mesh.m_BindPose.Length)
                        {
                            var verifiedBoneCount = sMesh.m_Bones.Count(x => x.TryGet(out _));
                            if (verifiedBoneCount > 0)
                            {
                                boneType = 1;
                            }
                            if (verifiedBoneCount != sMesh.m_Bones.Count)
                            {
                                //尝试使用m_BoneNameHashes 4.3 and up
                                if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                                {
                                    //有效bone数量是否大于SkinnedMeshRenderer
                                    var verifiedBoneCount2 = mesh.m_BoneNameHashes.Count(x => FixBonePath(GetPathFromHash(x)) != null);
                                    if (verifiedBoneCount2 > verifiedBoneCount)
                                    {
                                        boneType = 2;
                                    }
                                }
                            }
                        }
                    }
                    if (boneType == 0)
                    {
                        //尝试使用m_BoneNameHashes 4.3 and up
                        if (mesh.m_BindPose.Length > 0 && (mesh.m_BindPose.Length == mesh.m_BoneNameHashes?.Length))
                        {
                            var verifiedBoneCount = mesh.m_BoneNameHashes.Count(x => FixBonePath(GetPathFromHash(x)) != null);
                            if (verifiedBoneCount > 0)
                            {
                                boneType = 2;
                            }
                        }
                    }

                    if (boneType == 1)
                    {
                        var boneCount = sMesh.m_Bones.Count;
                        iMesh.BoneList = new List<ImportedBone>(boneCount);
                        for (int i = 0; i < boneCount; i++)
                        {
                            var bone = new ImportedBone();
                            if (sMesh.m_Bones[i].TryGet(out var m_Transform))
                            {
                                bone.Path = GetTransformPath(m_Transform);
                            }
                            var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                            bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                            iMesh.BoneList.Add(bone);
                        }
                    }
                    else if (boneType == 2)
                    {
                        var boneCount = mesh.m_BindPose.Length;
                        iMesh.BoneList = new List<ImportedBone>(boneCount);
                        for (int i = 0; i < boneCount; i++)
                        {
                            var bone = new ImportedBone();
                            var boneHash = mesh.m_BoneNameHashes[i];
                            var path = GetPathFromHash(boneHash);
                            bone.Path = FixBonePath(path);
                            var convert = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                            bone.Matrix = convert * mesh.m_BindPose[i] * convert;
                            iMesh.BoneList.Add(bone);
                        }
                    }

                    //Morphs
                    if (mesh.m_Shapes?.channels?.Count > 0)
                    {
                        var morph = new ImportedMorph();
                        MorphList.Add(morph);
                        morph.Path = iMesh.Path;
                        morph.Channels = new List<ImportedMorphChannel>(mesh.m_Shapes.channels.Count);
                        for (int i = 0; i < mesh.m_Shapes.channels.Count; i++)
                        {
                            var channel = new ImportedMorphChannel();
                            morph.Channels.Add(channel);
                            var shapeChannel = mesh.m_Shapes.channels[i];

                            var blendShapeName = "blendShape." + shapeChannel.name;
                            var crc = new SevenZip.CRC();
                            var bytes = Encoding.UTF8.GetBytes(blendShapeName);
                            crc.Update(bytes, 0, (uint)bytes.Length);
                            morphChannelNames[crc.GetDigest()] = blendShapeName;

                            morphChannelNames[shapeChannel.nameHash] = shapeChannel.name;

                            channel.Name = shapeChannel.name.Split('.').Last();
                            channel.KeyframeList = new List<ImportedMorphKeyframe>(shapeChannel.frameCount);
                            var frameEnd = shapeChannel.frameIndex + shapeChannel.frameCount;
                            for (int frameIdx = shapeChannel.frameIndex; frameIdx < frameEnd; frameIdx++)
                            {
                                var keyframe = new ImportedMorphKeyframe();
                                channel.KeyframeList.Add(keyframe);
                                keyframe.Weight = mesh.m_Shapes.fullWeights[frameIdx];
                                var shape = mesh.m_Shapes.shapes[frameIdx];
                                keyframe.hasNormals = shape.hasNormals;
                                keyframe.hasTangents = shape.hasTangents;
                                keyframe.VertexList = new List<ImportedMorphVertex>((int)shape.vertexCount);
                                var vertexEnd = shape.firstVertex + shape.vertexCount;
                                for (int j = (int)shape.firstVertex; j < vertexEnd; j++)
                                {
                                    var destVertex = new ImportedMorphVertex();
                                    keyframe.VertexList.Add(destVertex);
                                    var morphVertex = mesh.m_Shapes.vertices[j];
                                    destVertex.Index = morphVertex.index;
                                    var sourceVertex = iMesh.VertexList[(int)morphVertex.index];
                                    destVertex.Vertex = new ImportedVertex();
                                    var morphPos = morphVertex.vertex;
                                    destVertex.Vertex.Vertex = sourceVertex.Vertex + new Vector3(-morphPos.X, morphPos.Y, morphPos.Z);
                                    if (shape.hasNormals)
                                    {
                                        var morphNormal = morphVertex.normal;
                                        destVertex.Vertex.Normal = new Vector3(-morphNormal.X, morphNormal.Y, morphNormal.Z);
                                    }
                                    if (shape.hasTangents)
                                    {
                                        var morphTangent = morphVertex.tangent;
                                        destVertex.Vertex.Tangent = new Vector4(-morphTangent.X, morphTangent.Y, morphTangent.Z, 0);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            //TODO combine mesh
            if (combine)
            {
                meshR.m_GameObject.TryGet(out var m_GameObject);
                var frame = RootFrame.FindChild(m_GameObject.m_Name);
                if (frame != null)
                {
                    frame.LocalPosition = RootFrame.LocalPosition;
                    frame.LocalRotation = RootFrame.LocalRotation;
                    while (frame.Parent != null)
                    {
                        frame = frame.Parent;
                        frame.LocalPosition = RootFrame.LocalPosition;
                        frame.LocalRotation = RootFrame.LocalRotation;
                    }
                }
            }

            MeshList.Add(iMesh);
        }

        private static Mesh GetMesh(Renderer meshR)
        {
            if (meshR is SkinnedMeshRenderer sMesh)
            {
                if (sMesh.m_Mesh.TryGet(out var m_Mesh))
                {
                    return m_Mesh;
                }

                sMesh.m_GameObject.TryGet(out var gameObject);
                var externalName = GetExternalFileName(sMesh.m_Mesh.m_FileID, sMesh.assetsFile);
                Logger.Warning(
                    $"Unable to resolve SkinnedMeshRenderer mesh for {gameObject?.m_Name ?? "<unknown>"} " +
                    $"rendererPathId={sMesh.m_PathID} source={sMesh.assetsFile?.originalPath ?? sMesh.assetsFile?.fileName} " +
                    $"meshFileId={sMesh.m_Mesh.m_FileID} meshExternal={externalName} meshPathId={sMesh.m_Mesh.m_PathID}."
                );
            }
            else
            {
                meshR.m_GameObject.TryGet(out var m_GameObject);
                if (m_GameObject.m_MeshFilter != null)
                {
                    if (m_GameObject.m_MeshFilter.m_Mesh.TryGet(out var m_Mesh))
                    {
                        return m_Mesh;
                    }

                    Logger.Warning(
                        $"Unable to resolve MeshFilter mesh for {m_GameObject?.m_Name ?? "<unknown>"} " +
                        $"source={m_GameObject?.assetsFile?.originalPath ?? m_GameObject?.assetsFile?.fileName} " +
                        $"meshFileId={m_GameObject.m_MeshFilter.m_Mesh.m_FileID} " +
                        $"meshExternal={GetExternalFileName(m_GameObject.m_MeshFilter.m_Mesh.m_FileID, m_GameObject.m_MeshFilter.assetsFile)} " +
                        $"meshPathId={m_GameObject.m_MeshFilter.m_Mesh.m_PathID}."
                    );
                }
            }

            return null;
        }

        private static string GetExternalFileName(int fileId, SerializedFile assetsFile)
        {
            if (fileId == 0)
            {
                return assetsFile?.fileName;
            }
            if (assetsFile != null && fileId > 0 && fileId - 1 < assetsFile.m_Externals.Count)
            {
                return assetsFile.m_Externals[fileId - 1].fileName;
            }
            return null;
        }

        private string GetTransformPath(Transform transform)
        {
            if (transformDictionary.TryGetValue(transform, out var frame))
            {
                return frame.Path;
            }
            return null;
        }

        private string FixBonePath(AnimationClip m_AnimationClip, string path)
        {
            if (boundAnimationPathDic.TryGetValue(m_AnimationClip, out var basePath))
            {
                path = basePath + "/" + path;
            }
            return FixBonePath(path);
        }

        private string FixBonePath(string path)
        {
            var frame = RootFrame.FindFrameByPath(path);
            if (frame != null)
            {
                return frame.Path;
            }

            while (!string.IsNullOrEmpty(path))
            {
                var separatorIndex = path.IndexOf("/", StringComparison.Ordinal);
                if (separatorIndex < 0 || separatorIndex == path.Length - 1)
                {
                    break;
                }
                path = path.Substring(separatorIndex + 1);
                frame = RootFrame.FindFrameByPath(path);
                if (frame != null)
                {
                    return frame.Path;
                }
            }

            return null;
        }

        private static string GetTransformPathByFather(Transform transform)
        {
            transform.m_GameObject.TryGet(out var m_GameObject);
            if (transform.m_Father.TryGet(out var father))
            {
                return GetTransformPathByFather(father) + "/" + m_GameObject.m_Name;
            }

            return m_GameObject.m_Name;
        }

        private ImportedMaterial ConvertMaterial(Material mat)
        {
            ImportedMaterial iMat;
            if (mat != null)
            {
                if (options.exportMaterials)
                {
                    options.materials.Add(mat);
                }
                iMat = ImportedHelpers.FindMaterial(mat.m_Name, MaterialList);
                if (iMat != null)
                {
                    return iMat;
                }
                if (options.materialCache != null && options.materialCache.TryGetValue(mat, out var cachedMaterial))
                {
                    iMat = CloneMaterial(cachedMaterial);
                    var textureIndex = 0;
                    foreach (var texEnv in mat.m_SavedProperties.m_TexEnvs)
                    {
                        if (!texEnv.Value.m_Texture.TryGet<Texture2D>(out var m_Texture2D))
                        {
                            continue;
                        }
                        if (iMat.Textures != null && textureIndex < iMat.Textures.Count)
                        {
                            ConvertTexture2D(m_Texture2D, iMat.Textures[textureIndex].Name);
                        }
                        textureIndex++;
                    }
                    MaterialList.Add(iMat);
                    return iMat;
                }
                using (Measure("model_material", new Dictionary<string, object>
                {
                    ["material"] = mat.m_Name,
                    ["source"] = mat.assetsFile?.fullName,
                    ["pathId"] = mat.m_PathID,
                    ["textureSlotCount"] = mat.m_SavedProperties?.m_TexEnvs?.Count ?? 0,
                }))
                {
                    iMat = new ImportedMaterial();
                    iMat.Name = mat.m_Name;
                    //default values
                    iMat.Diffuse = new Color(0.8f, 0.8f, 0.8f, 1);
                    iMat.Ambient = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Emissive = new Color(0, 0, 0, 1);
                    iMat.Specular = new Color(0.2f, 0.2f, 0.2f, 1);
                    iMat.Reflection = new Color(0, 0, 0, 1);
                    iMat.Shininess = 20f;
                    iMat.Transparency = 0f;
                    iMat.UnityFloats = mat.m_SavedProperties.m_Floats.ToDictionary(x => x.Key, x => x.Value);
                    iMat.UnityColors = mat.m_SavedProperties.m_Colors.ToDictionary(x => x.Key, x => x.Value);
                    var hasColor = false;
                    foreach (var col in mat.m_SavedProperties.m_Colors)
                    {
                        switch (col.Key)
                        {
                            case "_Color":
                                iMat.Diffuse = col.Value;
                                hasColor = true;
                                break;
                            case "_BaseColor" when !hasColor:
                                // 很多 SRP/HDRP 材质用 _BaseColor 表示基础色；没有 _Color 时用它保证 glTF 预览可见。
                                iMat.Diffuse = col.Value;
                                break;
                            case "_SColor":
                                iMat.Ambient = col.Value;
                                break;
                            case "_EmissionColor":
                                iMat.Emissive = col.Value;
                                break;
                            case "_SpecularColor":
                                iMat.Specular = col.Value;
                                break;
                            case "_ReflectColor":
                                iMat.Reflection = col.Value;
                                break;
                        }
                    }

                    foreach (var flt in mat.m_SavedProperties.m_Floats)
                    {
                        switch (flt.Key)
                        {
                            case "_Shininess":
                                iMat.Shininess = flt.Value;
                                break;
                            case "_Transparency":
                                iMat.Transparency = flt.Value;
                                break;
                        }
                    }

                    //textures
                    iMat.Textures = new List<ImportedMaterialTexture>();
                    foreach (var texEnv in mat.m_SavedProperties.m_TexEnvs)
                    {
                        if (!texEnv.Value.m_Texture.TryGet<Texture2D>(out var m_Texture2D)) //TODO other Texture
                        {
                            if (!texEnv.Value.m_Texture.IsNull)
                            {
                                Logger.Warning($"Unable to resolve texture {texEnv.Key} for material {mat.m_Name}.");
                            }
                            continue;
                        }

                        var texture = new ImportedMaterialTexture();
                        iMat.Textures.Add(texture);

                        texture.Slot = texEnv.Key;
                        texture.Dest = GetTextureDestination(texEnv.Key);

                        var ext = GetTextureNameExtension();
                        if (textureNameDictionary.TryGetValue(m_Texture2D, out var textureName))
                        {
                            texture.Name = textureName;
                        }
                        else if (ImportedHelpers.FindTexture(m_Texture2D.m_Name + ext, TextureList) != null) //已有相同名字的图片
                        {
                            for (int i = 1; ; i++)
                            {
                                var name = m_Texture2D.m_Name + $" ({i}){ext}";
                                if (ImportedHelpers.FindTexture(name, TextureList) == null)
                                {
                                    texture.Name = name;
                                    textureNameDictionary.Add(m_Texture2D, name);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            texture.Name = m_Texture2D.m_Name + ext;
                            textureNameDictionary.Add(m_Texture2D, texture.Name);
                        }

                        texture.Offset = texEnv.Value.m_Offset;
                        texture.Scale = texEnv.Value.m_Scale;
                        ConvertTexture2D(m_Texture2D, texture.Name);
                    }
                }

                MaterialList.Add(iMat);
                options.materialCache?.TryAdd(mat, CloneMaterial(iMat));
            }
            else
            {
                iMat = new ImportedMaterial();
            }
            return iMat;
        }

        private int GetTextureDestination(string key)
        {
            if (options.texs.TryGetValue(key, out var target))
            {
                return target;
            }

            if (
                key == "_MainTex"
                || key.Contains("Diffuse")
                || key.Contains("Albedo")
                || key.Contains("BaseMap")
                || key.Contains("BaseColor")
            )
            {
                return 0;
            }

            if (key == "_BumpMap")
            {
                return 3;
            }

            if (key.Contains("Normal"))
            {
                return 1;
            }

            if (key.Contains("Specular"))
            {
                return 2;
            }

            if (key.Contains("Emission") || key.Contains("Emissive"))
            {
                return 5;
            }

            if (key.Contains("Reflect"))
            {
                return 6;
            }

            return -1;
        }

        private void ConvertTexture2D(Texture2D m_Texture2D, string name)
        {
            var iTex = ImportedHelpers.FindTexture(name, TextureList);
            if (iTex != null)
            {
                return;
            }

            var exportName = GetTextureExportName(m_Texture2D, name);
            if (
                options.textureCache != null
                && options.textureCache.TryGetValue(m_Texture2D, out var cachedTexture)
                && options.textureDataExists?.Invoke(m_Texture2D, cachedTexture.ExportName ?? exportName) == true
            )
            {
                var cached = new ImportedTexture(name, cachedTexture.ExportName ?? exportName);
                CopyTextureMetadata(cachedTexture, cached);
                FillTextureMetadata(cached, m_Texture2D);
                TextureList.Add(cached);
                return;
            }

            if (options.textureMode == TextureExportMode.Reference)
            {
                iTex = new ImportedTexture(name, exportName)
                {
                    IsReferenceOnly = true,
                };
                FillTextureMetadata(iTex, m_Texture2D);
                TextureList.Add(iTex);
                return;
            }

            if (options.textureDataExists?.Invoke(m_Texture2D, exportName) == true)
            {
                iTex = new ImportedTexture(name, exportName);
                FillTextureMetadata(iTex, m_Texture2D);
                TextureList.Add(iTex);
                options.textureCache?.TryAdd(m_Texture2D, iTex);
                return;
            }

            var stage = options.textureMode == TextureExportMode.Raw
                ? "model_texture_raw"
                : "model_texture";
            var textureProfileData = new Dictionary<string, object>
            {
                ["texture"] = m_Texture2D.m_Name,
                ["source"] = m_Texture2D.assetsFile?.fullName,
                ["pathId"] = m_Texture2D.m_PathID,
                ["exportName"] = exportName,
                ["textureMode"] = options.textureMode.ToString(),
                ["textureFormat"] = m_Texture2D.m_TextureFormat.ToString(),
                ["width"] = m_Texture2D.m_Width,
                ["height"] = m_Texture2D.m_Height,
                ["imageFormat"] = options.imageFormat.ToString(),
            };

            using (Measure(stage, textureProfileData))
            {
                if (options.textureMode == TextureExportMode.Raw)
                {
                    var data = m_Texture2D.image_data?.GetData();
                    if (data != null)
                    {
                        iTex = new ImportedTexture(data, name);
                        iTex.ExportName = exportName;
                        FillTextureMetadata(iTex, m_Texture2D);
                        TextureList.Add(iTex);
                    }
                }
                else
                {
                    var stream = m_Texture2D.ConvertToStream(options.imageFormat, true, options.profileMeasure, textureProfileData);
                    if (stream != null)
                    {
                        using (stream)
                        {
                            iTex = new ImportedTexture(stream, name);
                            iTex.ExportName = exportName;
                            FillTextureMetadata(iTex, m_Texture2D);
                            TextureList.Add(iTex);
                        }
                    }
                }
            }
        }

        private static void FillTextureMetadata(ImportedTexture importedTexture, Texture2D texture)
        {
            importedTexture.SourceTextureFormat = texture.m_TextureFormat.ToString();
            importedTexture.Width = texture.m_Width;
            importedTexture.Height = texture.m_Height;
            importedTexture.MipCount = texture.m_MipCount;
            importedTexture.SourcePathId = texture.m_PathID;
            importedTexture.SourceAssetPath = texture.assetsFile?.fullName;
            importedTexture.SourceFileName = texture.assetsFile?.fileName;
            importedTexture.UnityVersion = texture.version != null ? string.Join(".", texture.version) : null;
            importedTexture.Platform = texture.platform.ToString();
            importedTexture.RawDataSize = texture.image_data != null ? (int)texture.image_data.Size : 0;
        }

        private static void CopyTextureMetadata(ImportedTexture source, ImportedTexture destination)
        {
            destination.IsReferenceOnly = source.IsReferenceOnly;
            destination.SourceTextureFormat = source.SourceTextureFormat;
            destination.Width = source.Width;
            destination.Height = source.Height;
            destination.MipCount = source.MipCount;
            destination.SourcePathId = source.SourcePathId;
            destination.SourceAssetPath = source.SourceAssetPath;
            destination.SourceFileName = source.SourceFileName;
            destination.UnityVersion = source.UnityVersion;
            destination.Platform = source.Platform;
            destination.RawDataSize = source.RawDataSize;
        }

        private string GetTextureNameExtension()
        {
            return options.textureMode switch
            {
                TextureExportMode.Raw => ".rawtex",
                TextureExportMode.Reference => ".reftex",
                _ => $".{options.imageFormat.ToString().ToLower()}",
            };
        }

        private static string GetTextureExportName(Texture2D texture, string name)
        {
            var ext = System.IO.Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".png";
            }

            var rawName = System.IO.Path.GetFileNameWithoutExtension(name);
            return $"{rawName}_{texture.assetsFile.fileName}_{texture.m_PathID}{ext}";
        }

        private IDisposable Measure(string stage, IDictionary<string, object> data)
        {
            return options.profileMeasure?.Invoke(stage, data) ?? NullScope.Instance;
        }

        private static ImportedMaterial CloneMaterial(ImportedMaterial material)
        {
            return new ImportedMaterial
            {
                Name = material.Name,
                Diffuse = material.Diffuse,
                Ambient = material.Ambient,
                Specular = material.Specular,
                Emissive = material.Emissive,
                Reflection = material.Reflection,
                Shininess = material.Shininess,
                Transparency = material.Transparency,
                Textures = material.Textures?.Select(x => new ImportedMaterialTexture
                {
                    Name = x.Name,
                    Slot = x.Slot,
                    Dest = x.Dest,
                    Offset = x.Offset,
                    Scale = x.Scale,
                }).ToList(),
                UnityFloats = material.UnityFloats?.ToDictionary(x => x.Key, x => x.Value),
                UnityColors = material.UnityColors?.ToDictionary(x => x.Key, x => x.Value),
            };
        }

        private string GetMeshVertexCacheKey(Mesh mesh, ImportedMesh importedMesh)
        {
            var uvFlags = string.Join("", importedMesh.hasUV.Select(x => x ? "1" : "0"));
            var uvTypes = string.Join(",", importedMesh.uvType);
            return string.Join("|",
                mesh.assetsFile?.fullName,
                mesh.m_PathID,
                mesh.m_VertexCount,
                importedMesh.hasNormal,
                uvFlags,
                uvTypes,
                importedMesh.hasTangent,
                importedMesh.hasColor,
                mesh.m_Skin?.Count ?? 0);
        }

        private void AddMeshVertexCache(string key, List<ImportedVertex> vertices)
        {
            if (options.meshVertexCache.ContainsKey(key))
            {
                return;
            }

            if (
                options.meshVertexCacheOrder != null
                && options.maxMeshVertexCacheEntries > 0
                && options.meshVertexCache.Count >= options.maxMeshVertexCacheEntries
            )
            {
                var evictedKey = options.meshVertexCacheOrder.Dequeue();
                options.meshVertexCache.Remove(evictedKey);
            }

            options.meshVertexCache[key] = CloneVertexList(vertices);
            options.meshVertexCacheOrder?.Enqueue(key);
        }

        private static List<ImportedVertex> CloneVertexList(List<ImportedVertex> vertices)
        {
            return vertices.Select(CloneVertex).ToList();
        }

        private static ImportedVertex CloneVertex(ImportedVertex vertex)
        {
            return new ImportedVertex
            {
                Vertex = vertex.Vertex,
                Normal = vertex.Normal,
                UV = vertex.UV?.Select(x => x?.ToArray()).ToArray(),
                Tangent = vertex.Tangent,
                Color = vertex.Color,
                Weights = vertex.Weights?.ToArray(),
                BoneIndices = vertex.BoneIndices?.ToArray(),
            };
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }

        private void ConvertAnimations()
        {
            if (!options.exportAnimations)
            {
                return;
            }

            foreach (var animationClip in animationClipHashSet)
            {
                using var animationProfile = Measure("model_animation", new Dictionary<string, object>
                {
                    ["animation"] = animationClip.m_Name,
                    ["source"] = animationClip.assetsFile?.fullName,
                    ["pathId"] = animationClip.m_PathID,
                    ["sampleRate"] = animationClip.m_SampleRate,
                    ["legacy"] = animationClip.m_Legacy,
                });
                var iAnim = new ImportedKeyframedAnimation();
                var name = animationClip.m_Name;
                if (AnimationList.Exists(x => x.Name == name))
                {
                    for (int i = 1; ; i++)
                    {
                        var fixName = name + $"_{i}";
                        if (!AnimationList.Exists(x => x.Name == fixName))
                        {
                            name = fixName;
                            break;
                        }
                    }
                }
                iAnim.Name = name;
                iAnim.SampleRate = animationClip.m_SampleRate;
                iAnim.TrackList = new List<ImportedAnimationKeyframedTrack>();
                iAnim.HumanoidMuscles = new List<ImportedHumanoidMuscleCurve>();
                AnimationList.Add(iAnim);
                if (animationClip.m_Legacy)
                {
                    foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_CompressedRotationCurve.m_Path));

                        var numKeys = m_CompressedRotationCurve.m_Times.m_NumItems;
                        var data = m_CompressedRotationCurve.m_Times.UnpackInts();
                        var times = new float[numKeys];
                        int t = 0;
                        for (int i = 0; i < numKeys; i++)
                        {
                            t += data[i];
                            times[i] = t * 0.01f;
                        }
                        var quats = m_CompressedRotationCurve.m_Values.UnpackQuats();

                        for (int i = 0; i < numKeys; i++)
                        {
                            var quat = quats[i];
                            var value = new Quaternion(quat.X, -quat.Y, -quat.Z, quat.W);
                            track.Rotations.Add(new ImportedKeyframe<Quaternion>(times[i], value));
                        }
                    }
                    foreach (var m_RotationCurve in animationClip.m_RotationCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_RotationCurve.path));
                        foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                        {
                            var value = new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W);
                            track.Rotations.Add(new ImportedKeyframe<Quaternion>(m_Curve.time, value));
                        }
                    }
                    foreach (var m_PositionCurve in animationClip.m_PositionCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_PositionCurve.path));
                        foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                        {
                            track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                        }
                    }
                    foreach (var m_ScaleCurve in animationClip.m_ScaleCurves)
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_ScaleCurve.path));
                        foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                        {
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z)));
                        }
                    }
                    if (animationClip.m_EulerCurves != null)
                    {
                        foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                        {
                            var track = iAnim.FindTrack(FixBonePath(animationClip, m_EulerCurve.path));
                            foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                            {
                                var value = Fbx.EulerToQuaternion(new Vector3(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z));
                                track.Rotations.Add(new ImportedKeyframe<Quaternion>(m_Curve.time, value));
                            }
                        }
                    }
                    foreach (var m_FloatCurve in animationClip.m_FloatCurves)
                    {
                        if (m_FloatCurve.classID == ClassIDType.SkinnedMeshRenderer) //BlendShape
                        {
                            var channelName = m_FloatCurve.attribute;
                            int dotPos = channelName.IndexOf('.');
                            if (dotPos >= 0)
                            {
                                channelName = channelName.Substring(dotPos + 1);
                            }

                            var path = GetPathByChannelName(channelName);
                            if (string.IsNullOrEmpty(path))
                            {
                                path = FixBonePath(animationClip, m_FloatCurve.path);
                            }
                            var track = iAnim.FindTrack(path, channelName);
                            if (track.BlendShape == null)
                            {
                                track.BlendShape = new ImportedBlendShape();
                                track.BlendShape.ChannelName = channelName;
                            }
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                track.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value));
                            }
                        }
                    }
                }
                else
                {
                    var m_Clip = animationClip.m_MuscleClip.m_Clip;
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    var m_ClipBindingConstant = animationClip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();
                    var m_ACLClip = m_Clip.m_ACLClip;
                    var aclCount = m_ACLClip.CurveCount;
                    if (m_ACLClip.IsSet && !options.game.Type.IsSRGroup())
                    {
                        m_ACLClip.Process(options.game, out var values, out var times);
                        for (int frameIndex = 0; frameIndex < times.Length; frameIndex++)
                        {
                            var time = times[frameIndex];
                            var frameOffset = frameIndex * m_ACLClip.CurveCount;
                            for (int curveIndex = 0; curveIndex < m_ACLClip.CurveCount;)
                            {
                                var index = curveIndex;
                                ReadCurveData(animationClip, iAnim, m_ClipBindingConstant, index, time, values, (int)frameOffset, ref curveIndex);
                            }

                        }
                    }
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        var streamedValues = frame.keyList.Select(x => x.value).ToArray();
                        for (int curveIndex = 0; curveIndex < frame.keyList.Count;)
                        {
                            var index = frame.keyList[curveIndex].index;
                            if (!options.game.Type.IsSRGroup())
                                index += (int)aclCount;
                            ReadCurveData(animationClip, iAnim, m_ClipBindingConstant, index, frame.time, streamedValues, 0, ref curveIndex);
                        }
                    }
                    var m_DenseClip = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip.curveCount;
                    for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                    {
                        var time = m_DenseClip.m_BeginTime + frameIndex / m_DenseClip.m_SampleRate;
                        var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                        for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                        {
                            var index = streamCount + curveIndex;
                            if (!options.game.Type.IsSRGroup())
                                index += (int)aclCount;
                            ReadCurveData(animationClip, iAnim, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                        }
                    }
                    if (m_ACLClip.IsSet && options.game.Type.IsSRGroup())
                    {
                        m_ACLClip.Process(options.game, out var values, out var times);
                        for (int frameIndex = 0; frameIndex < times.Length; frameIndex++)
                        {
                            var time = times[frameIndex];
                            var frameOffset = frameIndex * m_ACLClip.CurveCount;
                            for (int curveIndex = 0; curveIndex < m_ACLClip.CurveCount;)
                            {
                                var index = (int)(curveIndex + m_DenseClip.m_CurveCount + streamCount);
                                ReadCurveData(animationClip, iAnim, m_ClipBindingConstant, index, time, values, (int)frameOffset, ref curveIndex);
                            }

                        }
                    }
                    if (m_Clip.m_ConstantClip != null)
                    {
                        var m_ConstantClip = m_Clip.m_ConstantClip;
                        var denseCount = m_Clip.m_DenseClip.m_CurveCount;
                        var time2 = 0.0f;
                        for (int i = 0; i < 2; i++)
                        {
                            for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                            {
                                var index = aclCount + streamCount + denseCount + curveIndex;
                                ReadCurveData(animationClip, iAnim, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                            }
                            time2 = animationClip.m_MuscleClip.m_StopTime;
                        }
                    }
                }

                BakeHumanoidMusclesToSkeleton(iAnim);
                PruneAuxiliaryTracksForExplicitHumanoidAnimation(iAnim);
            }
        }

        private void PruneAuxiliaryTracksForExplicitHumanoidAnimation(ImportedKeyframedAnimation animation)
        {
            if (!options.preferBakedHumanoidBodyAnimation
                || animation?.HumanoidMusclesBaked != true
                || animation.HumanoidBakeDiagnostics?.Targets == null
                || animation.TrackList == null)
            {
                return;
            }

            var keepPaths = animation.HumanoidBakeDiagnostics.Targets
                .Where(x => string.Equals(x.Status, "baked_approximate", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SkeletonPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            if (avatar?.m_HumanDescription?.m_Human != null)
            {
                var hipsPath = avatar.m_HumanDescription.m_Human
                    .Where(x => string.Equals(x.m_HumanName, "Hips", StringComparison.OrdinalIgnoreCase))
                    .Select(x => RootFrame?.FindFrame(x.m_BoneName)?.Path)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                if (!string.IsNullOrWhiteSpace(hipsPath))
                {
                    keepPaths.Add(hipsPath);
                }
            }

            if (keepPaths.Count == 0)
            {
                return;
            }

            animation.TrackList = animation.TrackList
                .Where(track =>
                    track.BlendShape != null
                    || (!string.IsNullOrWhiteSpace(track.Path) && keepPaths.Contains(track.Path)))
                .ToList();
        }

        private void BakeHumanoidMusclesToSkeleton(ImportedKeyframedAnimation animation)
        {
            if (animation?.HumanoidMuscles == null || animation.HumanoidMuscles.Count == 0 || avatar?.m_HumanDescription?.m_Human == null)
            {
                return;
            }

            animation.HumanoidBakeDiagnostics = new ImportedHumanoidBakeDiagnostics
            {
                Mode = "ApproximateHumanoidMuscleV3",
                Solver = string.IsNullOrWhiteSpace(options.humanoidBakeSolver)
                    ? "AvatarPreEulerPost"
                    : options.humanoidBakeSolver,
                HumanBoneCount = avatar.m_HumanDescription.m_Human.Count,
                TargetCount = HumanoidMuscleBakeTarget.Targets.Length,
                Notes = new[]
                {
                    "This bake is diagnostic and approximate. It applies Unity Avatar axes, pre/post rotations, signs, and limits, but it is not the native Humanoid solver.",
                    "RootT is intentionally not baked into Hips translation because Unity Humanoid root motion needs a separate coordinate/root-motion solve.",
                    "Use this output to inspect mapping coverage; do not treat it as final animation correctness.",
                },
            };

            var curves = animation.HumanoidMuscles
                .Where(x => !string.IsNullOrWhiteSpace(x.Attribute) && x.Keyframes != null && x.Keyframes.Count > 0)
                .GroupBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            if (curves.Count == 0)
            {
                animation.HumanoidBakeDiagnostics.Status = "no_curves";
                return;
            }

            var times = curves.Values
                .SelectMany(x => x.Keyframes.Select(k => k.time))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (times.Length == 0)
            {
                animation.HumanoidBakeDiagnostics.Status = "no_sample_times";
                return;
            }
            animation.HumanoidBakeDiagnostics.SampleTimeCount = times.Length;

            var humanBoneToFrame = avatar.m_HumanDescription.m_Human
                .Where(x => !string.IsNullOrWhiteSpace(x.m_HumanName) && !string.IsNullOrWhiteSpace(x.m_BoneName))
                .Select(x => new { x.m_HumanName, Frame = RootFrame?.FindFrame(x.m_BoneName) })
                .Where(x => x.Frame != null)
                .GroupBy(x => x.m_HumanName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Frame, StringComparer.OrdinalIgnoreCase);
            if (humanBoneToFrame.Count == 0)
            {
                animation.HumanoidBakeDiagnostics.Status = "no_human_bone_frames";
                return;
            }

            var bakedTrackCount = 0;
            var bakedKeyframeCount = 0;
            foreach (var target in HumanoidMuscleBakeTarget.Targets)
            {
                var attributes = new[] { target.XAttribute, target.YAttribute, target.ZAttribute }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                var diagnostic = new ImportedHumanoidBakeTargetDiagnostic
                {
                    HumanBone = target.HumanBone,
                    Attributes = attributes,
                };
                animation.HumanoidBakeDiagnostics.Targets.Add(diagnostic);
                FillAvatarAxisDiagnostics(target.HumanBone, diagnostic);

                if (!humanBoneToFrame.TryGetValue(target.HumanBone, out var frame))
                {
                    diagnostic.HasFrame = false;
                    diagnostic.HasCurves = attributes.Any(curves.ContainsKey);
                    diagnostic.Status = "missing_human_bone_frame";
                    continue;
                }
                diagnostic.HasFrame = true;
                diagnostic.SkeletonPath = frame.Path;

                var hasCurve = curves.ContainsKey(target.XAttribute)
                    || curves.ContainsKey(target.YAttribute)
                    || curves.ContainsKey(target.ZAttribute);
                diagnostic.HasCurves = hasCurve;
                if (!hasCurve)
                {
                    diagnostic.Status = "no_matching_muscle_curve";
                    continue;
                }

                var track = animation.FindTrack(frame.Path);
                var rest = NormalizeQuaternion(frame.LocalRotation);
                foreach (var time in times)
                {
                    Quaternion delta;
                    if (!TryBuildAvatarAxisDelta(curves, target, diagnostic, time, options.humanoidBakeSolver, out delta))
                    {
                        var x = SampleCurve(curves, target.XAttribute, time) * target.XScale;
                        var y = SampleCurve(curves, target.YAttribute, time) * target.YScale;
                        var z = SampleCurve(curves, target.ZAttribute, time) * target.ZScale;
                        delta = EulerDegreesToQuaternion(new Vector3(x, y, z));
                    }
                    var solver = options.humanoidBakeSolver ?? string.Empty;
                    if (solver.IndexOf("InvertDelta", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        delta = Inverse(delta);
                    }
                    var rotation = solver.IndexOf("RestBeforeDelta", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Multiply(rest, delta)
                        : Multiply(delta, rest);
                    track.Rotations.Add(new ImportedKeyframe<Quaternion>(time, NormalizeQuaternion(rotation)));
                    bakedKeyframeCount++;
                }
                bakedTrackCount++;
                diagnostic.Status = "baked_approximate";
            }

            if ((options.humanoidBakeSolver ?? string.Empty).IndexOf("RootQToHips", StringComparison.OrdinalIgnoreCase) >= 0
                && humanBoneToFrame.TryGetValue("Hips", out var hipsFrame))
            {
                if (TryBakeRootQToFrame(curves, times, hipsFrame, animation))
                {
                    bakedTrackCount++;
                    bakedKeyframeCount += times.Length;
                }
            }

            animation.HumanoidBakeDiagnostics.MappedTargetCount = animation.HumanoidBakeDiagnostics.Targets.Count(x => x.Status == "baked_approximate");
            animation.HumanoidBakeDiagnostics.MissingTargetCount = animation.HumanoidBakeDiagnostics.Targets.Count - animation.HumanoidBakeDiagnostics.MappedTargetCount;

            if (bakedTrackCount > 0)
            {
                animation.HumanoidMusclesBaked = true;
                animation.HumanoidBakeMode = "ApproximateHumanoidMuscleV3";
                animation.HumanoidBakedTrackCount = bakedTrackCount;
                animation.HumanoidBakedKeyframeCount = bakedKeyframeCount;
                animation.HumanoidBakeDiagnostics.Status = "experimental_baked";
            }
            else
            {
                animation.HumanoidBakeDiagnostics.Status = "no_tracks_baked";
            }
        }

        private static bool TryBakeRootQToFrame(
            Dictionary<string, ImportedHumanoidMuscleCurve> curves,
            float[] times,
            ImportedFrame frame,
            ImportedKeyframedAnimation animation)
        {
            if (curves == null
                || times == null
                || times.Length == 0
                || frame == null
                || animation == null
                || !curves.ContainsKey("RootQ.w"))
            {
                return false;
            }

            var track = animation.FindTrack(frame.Path);
            var rest = NormalizeQuaternion(frame.LocalRotation);
            var first = SampleRootQ(curves, times[0]);
            var firstInv = Inverse(first);
            foreach (var time in times)
            {
                var current = SampleRootQ(curves, time);
                var delta = NormalizeQuaternion(Multiply(current, firstInv));
                track.Rotations.Add(new ImportedKeyframe<Quaternion>(time, NormalizeQuaternion(Multiply(delta, rest))));
            }
            return true;
        }

        private static Quaternion SampleRootQ(Dictionary<string, ImportedHumanoidMuscleCurve> curves, float time)
        {
            var qx = SampleCurve(curves, "RootQ.x", time);
            var qy = SampleCurve(curves, "RootQ.y", time);
            var qz = SampleCurve(curves, "RootQ.z", time);
            var qw = SampleCurve(curves, "RootQ.w", time);
            return NormalizeQuaternion(new Quaternion(qx, -qy, -qz, qw));
        }

        private void FillAvatarAxisDiagnostics(string humanBoneName, ImportedHumanoidBakeTargetDiagnostic diagnostic)
        {
            if (diagnostic == null
                || string.IsNullOrWhiteSpace(humanBoneName)
                || avatar?.m_Avatar?.m_Human?.m_Skeleton?.m_Node == null
                || avatar.m_Avatar.m_Human.m_Skeleton.m_AxesArray == null
                || avatar.m_Avatar.m_Human.m_HumanBoneIndex == null)
            {
                return;
            }

            if (!Enum.TryParse<BoneType>(humanBoneName, out var boneType))
            {
                return;
            }

            var humanBoneIndex = (int)boneType;
            diagnostic.HumanBoneIndex = humanBoneIndex;
            if (humanBoneIndex < 0 || humanBoneIndex >= avatar.m_Avatar.m_Human.m_HumanBoneIndex.Length)
            {
                return;
            }

            var skeletonNodeIndex = avatar.m_Avatar.m_Human.m_HumanBoneIndex[humanBoneIndex];
            diagnostic.AvatarSkeletonNodeIndex = skeletonNodeIndex;
            if (skeletonNodeIndex < 0 || skeletonNodeIndex >= avatar.m_Avatar.m_Human.m_Skeleton.m_Node.Count)
            {
                return;
            }

            var node = avatar.m_Avatar.m_Human.m_Skeleton.m_Node[skeletonNodeIndex];
            diagnostic.AvatarAxesId = node.m_AxesId;
            if (node.m_AxesId < 0 || node.m_AxesId >= avatar.m_Avatar.m_Human.m_Skeleton.m_AxesArray.Count)
            {
                return;
            }

            var axes = avatar.m_Avatar.m_Human.m_Skeleton.m_AxesArray[node.m_AxesId];
            diagnostic.AvatarPreQ = ToArray(axes.m_PreQ);
            diagnostic.AvatarPostQ = ToArray(axes.m_PostQ);
            diagnostic.AvatarSgn = ToArray3(axes.m_Sgn);
            diagnostic.AvatarLimitMin = ToArray3(axes.m_Limit?.m_Min);
            diagnostic.AvatarLimitMax = ToArray3(axes.m_Limit?.m_Max);
        }

        private static bool TryBuildAvatarAxisDelta(
            Dictionary<string, ImportedHumanoidMuscleCurve> curves,
            HumanoidMuscleBakeTarget target,
            ImportedHumanoidBakeTargetDiagnostic diagnostic,
            float time,
            string solver,
            out Quaternion delta)
        {
            delta = Quaternion.Zero;
            if (target == null
                || diagnostic?.AvatarPreQ == null
                || diagnostic.AvatarPostQ == null
                || diagnostic.AvatarSgn == null
                || diagnostic.AvatarLimitMin == null
                || diagnostic.AvatarLimitMax == null)
            {
                return false;
            }

            var axisOrder = GetAvatarAxisOrder(solver);
            var muscleValues = new[]
            {
                SampleCurve(curves, target.XAttribute, time),
                SampleCurve(curves, target.YAttribute, time),
                SampleCurve(curves, target.ZAttribute, time),
            };
            ApplyPrimaryOnlyMuscleFilter(target, solver, muscleValues);
            var angles = new float[3];
            for (var muscleAxis = 0; muscleAxis < 3; muscleAxis++)
            {
                angles[axisOrder[muscleAxis]] = LimitMuscle(
                    muscleValues[muscleAxis],
                    diagnostic.AvatarLimitMin,
                    diagnostic.AvatarLimitMax,
                    diagnostic.AvatarSgn,
                    axisOrder[muscleAxis],
                    solver);
            }
            var x = angles[0];
            var y = angles[1];
            var z = angles[2];

            var pre = ToQuaternion(diagnostic.AvatarPreQ);
            var post = ToQuaternion(diagnostic.AvatarPostQ);
            var euler = EulerRadiansToQuaternion(new Vector3(x, y, z));
            var pair = (solver ?? string.Empty).Trim();
            Quaternion zero;
            Quaternion posed;
            if (string.Equals(pair, "AvatarPostEulerPre", StringComparison.OrdinalIgnoreCase))
            {
                zero = NormalizeQuaternion(Multiply(post, pre));
                posed = NormalizeQuaternion(Multiply(Multiply(post, euler), pre));
            }
            else if (string.Equals(pair, "AvatarPrePostEuler", StringComparison.OrdinalIgnoreCase))
            {
                zero = NormalizeQuaternion(Multiply(pre, post));
                posed = NormalizeQuaternion(Multiply(Multiply(pre, post), euler));
            }
            else if (string.Equals(pair, "AvatarEulerPrePost", StringComparison.OrdinalIgnoreCase))
            {
                zero = NormalizeQuaternion(Multiply(pre, post));
                posed = NormalizeQuaternion(Multiply(euler, Multiply(pre, post)));
            }
            else
            {
                zero = NormalizeQuaternion(Multiply(pre, post));
                posed = NormalizeQuaternion(Multiply(Multiply(pre, euler), post));
            }
            delta = NormalizeQuaternion(Multiply(posed, Inverse(zero)));
            return true;
        }

        private static void ApplyPrimaryOnlyMuscleFilter(HumanoidMuscleBakeTarget target, string solver, float[] muscleValues)
        {
            if (target == null
                || muscleValues == null
                || muscleValues.Length < 3
                || (solver ?? string.Empty).IndexOf("PrimaryOnly", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            switch (target.HumanBone)
            {
                case "LeftLowerLeg":
                case "RightLowerLeg":
                    muscleValues[0] = 0;
                    muscleValues[1] = 0;
                    muscleValues[2] = 0;
                    break;
                case "LeftUpperLeg":
                case "RightUpperLeg":
                case "LeftFoot":
                case "RightFoot":
                case "LeftUpperArm":
                case "RightUpperArm":
                case "LeftLowerArm":
                case "RightLowerArm":
                case "LeftHand":
                case "RightHand":
                    muscleValues[1] = 0;
                    muscleValues[2] = 0;
                    break;
            }
        }

        private static int[] GetAvatarAxisOrder(string solver)
        {
            var normalized = (solver ?? string.Empty).ToUpperInvariant();
            if (normalized.Contains("AXISXZY")) return new[] { 0, 2, 1 };
            if (normalized.Contains("AXISYXZ")) return new[] { 1, 0, 2 };
            if (normalized.Contains("AXISYZX")) return new[] { 1, 2, 0 };
            if (normalized.Contains("AXISZXY")) return new[] { 2, 0, 1 };
            if (normalized.Contains("AXISZYX")) return new[] { 2, 1, 0 };
            return new[] { 0, 1, 2 };
        }

        private static float LimitMuscle(float value, float[] min, float[] max, float[] sign, int axis, string solver)
        {
            if (axis < 0 || min == null || max == null || sign == null || axis >= min.Length || axis >= max.Length || axis >= sign.Length)
            {
                return 0;
            }

            var range = value >= 0 ? max[axis] : -min[axis];
            var signValue = sign[axis];
            var normalized = solver ?? string.Empty;
            if (normalized.IndexOf("NoSign", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                signValue = 1;
            }
            else if (normalized.IndexOf("InvertSign", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                signValue = -signValue;
            }
            return value * range * signValue;
        }

        private static float SampleCurve(Dictionary<string, ImportedHumanoidMuscleCurve> curves, string attribute, float time)
        {
            if (string.IsNullOrWhiteSpace(attribute) || !curves.TryGetValue(attribute, out var curve) || curve.Keyframes.Count == 0)
            {
                return 0;
            }

            var keys = curve.Keyframes.OrderBy(x => x.time).ToArray();
            if (time <= keys[0].time)
            {
                return keys[0].value;
            }
            if (time >= keys[keys.Length - 1].time)
            {
                return keys[keys.Length - 1].value;
            }

            for (var i = 1; i < keys.Length; i++)
            {
                if (time > keys[i].time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.time - a.time;
                var t = span <= 0 ? 0 : (time - a.time) / span;
                return a.value + (b.value - a.value) * t;
            }
            return keys[keys.Length - 1].value;
        }

        private static float[] ToArray(Vector4 value)
        {
            return new[] { value.X, value.Y, value.Z, value.W };
        }

        private static float[] ToArray3(object value)
        {
            return value switch
            {
                Vector3 v => new[] { v.X, v.Y, v.Z },
                Vector4 v => new[] { v.X, v.Y, v.Z },
                _ => null,
            };
        }

        private static Quaternion ToQuaternion(float[] value)
        {
            return value == null || value.Length < 4
                ? Quaternion.Zero
                : NormalizeQuaternion(new Quaternion(value[0], value[1], value[2], value[3]));
        }

        private static Quaternion EulerRadiansToQuaternion(Vector3 radians)
        {
            var halfX = radians.X / 2.0;
            var halfY = radians.Y / 2.0;
            var halfZ = radians.Z / 2.0;
            var sx = Math.Sin(halfX);
            var cx = Math.Cos(halfX);
            var sy = Math.Sin(halfY);
            var cy = Math.Cos(halfY);
            var sz = Math.Sin(halfZ);
            var cz = Math.Cos(halfZ);

            return NormalizeQuaternion(new Quaternion(
                (float)(sx * cy * cz + cx * sy * sz),
                (float)(cx * sy * cz - sx * cy * sz),
                (float)(cx * cy * sz + sx * sy * cz),
                (float)(cx * cy * cz - sx * sy * sz)
            ));
        }

        private static Quaternion EulerDegreesToQuaternion(Vector3 degrees)
        {
            var halfX = degrees.X * Math.PI / 360.0;
            var halfY = degrees.Y * Math.PI / 360.0;
            var halfZ = degrees.Z * Math.PI / 360.0;
            var sx = Math.Sin(halfX);
            var cx = Math.Cos(halfX);
            var sy = Math.Sin(halfY);
            var cy = Math.Cos(halfY);
            var sz = Math.Sin(halfZ);
            var cz = Math.Cos(halfZ);

            return NormalizeQuaternion(new Quaternion(
                (float)(sx * cy * cz + cx * sy * sz),
                (float)(cx * sy * cz - sx * cy * sz),
                (float)(cx * cy * sz + sx * sy * cz),
                (float)(cx * cy * cz - sx * sy * sz)
            ));
        }

        private static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }

        private static Quaternion Inverse(Quaternion q)
        {
            var lengthSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (lengthSq <= 0.000001)
            {
                return Quaternion.Zero;
            }
            var inv = 1.0f / lengthSq;
            return new Quaternion(-q.X * inv, -q.Y * inv, -q.Z * inv, q.W * inv);
        }

        private static Quaternion NormalizeQuaternion(Quaternion q)
        {
            var length = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (length <= 0.000001)
            {
                return Quaternion.Zero;
            }
            var inv = 1.0 / length;
            return new Quaternion((float)(q.X * inv), (float)(q.Y * inv), (float)(q.Z * inv), (float)(q.W * inv));
        }

        private sealed record HumanoidMuscleBakeTarget(
            string HumanBone,
            string XAttribute,
            string YAttribute,
            string ZAttribute,
            float XScale = 35,
            float YScale = 35,
            float ZScale = 35
        )
        {
            public static readonly HumanoidMuscleBakeTarget[] Targets =
            {
                new("Spine", "Spine Front-Back", "Spine Left-Right", "Spine Twist Left-Right", -25, 25, -35),
                new("Chest", "Chest Front-Back", "Chest Left-Right", "Chest Twist Left-Right", -25, 25, -35),
                new("UpperChest", "UpperChest Front-Back", "UpperChest Left-Right", "UpperChest Twist Left-Right", -25, 25, -35),
                new("Neck", "Neck Nod Down-Up", "Neck Tilt Left-Right", "Neck Turn Left-Right", -30, 30, -45),
                new("Head", "Head Nod Down-Up", "Head Tilt Left-Right", "Head Turn Left-Right", -35, 35, -55),
                new("LeftUpperLeg", "Left Upper Leg Front-Back", "Left Upper Leg In-Out", "Left Upper Leg Twist In-Out", -65, 35, -35),
                new("RightUpperLeg", "Right Upper Leg Front-Back", "Right Upper Leg In-Out", "Right Upper Leg Twist In-Out", -65, -35, 35),
                new("LeftLowerLeg", "Left Lower Leg Stretch", null, "Left Lower Leg Twist In-Out", -75, 0, -25),
                new("RightLowerLeg", "Right Lower Leg Stretch", null, "Right Lower Leg Twist In-Out", -75, 0, 25),
                new("LeftFoot", "Left Foot Up-Down", null, "Left Foot Twist In-Out", -45, 0, -35),
                new("RightFoot", "Right Foot Up-Down", null, "Right Foot Twist In-Out", -45, 0, 35),
                new("LeftToes", "Left Toes Up-Down", null, null, -35, 0, 0),
                new("RightToes", "Right Toes Up-Down", null, null, -35, 0, 0),
                new("LeftShoulder", "Left Shoulder Down-Up", "Left Shoulder Front-Back", null, -35, 35, 0),
                new("RightShoulder", "Right Shoulder Down-Up", "Right Shoulder Front-Back", null, -35, -35, 0),
                new("LeftUpperArm", "Left Arm Down-Up", "Left Arm Front-Back", "Left Arm Twist In-Out", -75, 45, -45),
                new("RightUpperArm", "Right Arm Down-Up", "Right Arm Front-Back", "Right Arm Twist In-Out", -75, -45, 45),
                new("LeftLowerArm", "Left Forearm Stretch", null, "Left Forearm Twist In-Out", -95, 0, -45),
                new("RightLowerArm", "Right Forearm Stretch", null, "Right Forearm Twist In-Out", -95, 0, 45),
                new("LeftHand", "Left Hand Down-Up", "Left Hand In-Out", null, -45, 35, 0),
                new("RightHand", "Right Hand Down-Up", "Right Hand In-Out", null, -45, -35, 0),
                new("Jaw", "Jaw Close", "Jaw Left-Right", null, -20, 20, 0),
            };
        }

        private void ReadCurveData(AnimationClip animationClip, ImportedKeyframedAnimation iAnim, AnimationClipBindingConstant m_ClipBindingConstant, int index, float time, float[] data, int offset, ref int curveIndex)
        {
            var binding = m_ClipBindingConstant.FindBinding(index);
            if (binding.typeID == ClassIDType.SkinnedMeshRenderer) //BlendShape
            {
                var channelName = GetChannelNameFromHash(binding.attribute);
                if (string.IsNullOrEmpty(channelName))
                {
                    curveIndex++;
                    return;
                }
                int dotPos = channelName.IndexOf('.');
                if (dotPos >= 0)
                {
                    channelName = channelName.Substring(dotPos + 1);
                }

                var path = GetPathByChannelName(channelName);
                if (string.IsNullOrEmpty(path))
                {
                    path = FixBonePath(GetPathFromHash(binding.path, animationClip));
                }
                var track = iAnim.FindTrack(path, channelName);
                if (track.BlendShape == null)
                {
                    track.BlendShape = new ImportedBlendShape();
                    track.BlendShape.ChannelName = channelName;
                }
                track.BlendShape.Keyframes.Add(new ImportedKeyframe<float>(time, data[curveIndex++ + offset]));
            }
            else if (binding.typeID == ClassIDType.Transform)
            {
                var path = FixBonePath(GetPathFromHash(binding.path, animationClip));
                var track = iAnim.FindTrack(path);

                switch (binding.attribute)
                {
                    case 1:
                        track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                        (
                            -data[curveIndex++ + offset],
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        )));
                        break;
                    case 2:
                        track.Rotations.Add(new ImportedKeyframe<Quaternion>(time, new Quaternion
                        (
                            data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        )));
                        break;
                    case 3:
                        track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                        (
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset],
                            data[curveIndex++ + offset]
                        )));
                        break;
                    case 4:
                        var value = Fbx.EulerToQuaternion(new Vector3
                        (
                            data[curveIndex++ + offset],
                            -data[curveIndex++ + offset],
                            -data[curveIndex++ + offset]
                        ));
                        track.Rotations.Add(new ImportedKeyframe<Quaternion>(time, value));
                        break;
                    default:
                        curveIndex++;
                        break;
                }
            }
            else if ((BindingCustomType)binding.customType == BindingCustomType.AnimatorMuscle)
            {
                var muscle = binding.GetHumanoidMuscle();
                var muscleIndex = (int)muscle;
                var attribute = muscle.ToAttributeString();
                var curve = iAnim.HumanoidMuscles.FirstOrDefault(x => x.Attribute == attribute);
                if (curve == null)
                {
                    curve = new ImportedHumanoidMuscleCurve { Attribute = attribute };
                    iAnim.HumanoidMuscles.Add(curve);
                }
                curve.BindingIndex = index;
                curve.MuscleIndex = muscleIndex;
                var value = data[curveIndex++ + offset];
                var referencePose = animationClip.m_MuscleClip?.m_ValueArrayReferencePose;
                if (referencePose != null)
                {
                    if ((options.humanoidBakeSolver ?? string.Empty).IndexOf("ReferenceByBindingIndex", StringComparison.OrdinalIgnoreCase) >= 0
                        && index >= 0
                        && index < referencePose.Length)
                    {
                        curve.ReferencePoseValue = referencePose[index];
                        value += referencePose[index];
                    }
                    else if ((options.humanoidBakeSolver ?? string.Empty).IndexOf("ReferenceByMuscleAttribute", StringComparison.OrdinalIgnoreCase) >= 0
                        && muscleIndex >= 0
                        && muscleIndex < referencePose.Length)
                    {
                        curve.ReferencePoseValue = referencePose[muscleIndex];
                        value += referencePose[muscleIndex];
                    }
                }
                curve.Keyframes.Add(new ImportedKeyframe<float>(time, value));
            }
            else
            {
                curveIndex++;
            }
        }

        private string GetPathFromHash(uint hash, AnimationClip animationClip = null)
        {
            bonePathHash.TryGetValue(hash, out var boneName);
            if (string.IsNullOrEmpty(boneName))
            {
                boneName = avatar?.FindBonePath(hash);
            }
            if (string.IsNullOrEmpty(boneName) && animationClip != null)
            {
                if (!animationTosCache.TryGetValue(animationClip, out var tos))
                {
                    tos = animationClip.FindTOS();
                    animationTosCache[animationClip] = tos;
                }
                tos.TryGetValue(hash, out boneName);
            }
            if (string.IsNullOrEmpty(boneName))
            {
                boneName = "unknown " + hash;
            }
            return boneName;
        }

        private void CreateBonePathHash(Transform m_Transform)
        {
            var name = GetTransformPathByFather(m_Transform);
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(name);
            crc.Update(bytes, 0, (uint)bytes.Length);
            bonePathHash[crc.GetDigest()] = name;
            int index;
            while ((index = name.IndexOf("/", StringComparison.Ordinal)) >= 0)
            {
                name = name.Substring(index + 1);
                crc = new SevenZip.CRC();
                bytes = Encoding.UTF8.GetBytes(name);
                crc.Update(bytes, 0, (uint)bytes.Length);
                bonePathHash[crc.GetDigest()] = name;
            }
            foreach (var pptr in m_Transform.m_Children)
            {
                if (pptr.TryGet(out var child))
                    CreateBonePathHash(child);
            }
        }

        private void DeoptimizeTransformHierarchy()
        {
            if (avatar == null)
                throw new Exception("Transform hierarchy has been optimized, but can't find Avatar to deoptimize.");
            // 1. Figure out the skeletonPaths from the unstripped avatar
            var skeletonPaths = new List<string>();
            foreach (var id in avatar.m_Avatar.m_AvatarSkeleton.m_ID)
            {
                var path = avatar.FindBonePath(id);
                skeletonPaths.Add(path);
            }
            // 2. Restore the original transform hierarchy
            // Prerequisite: skeletonPaths follow pre-order traversal
            for (var i = 1; i < skeletonPaths.Count; i++) // start from 1, skip the root transform because it will always be there.
            {
                var path = skeletonPaths[i];
                var strs = path.Split('/');
                string transformName;
                ImportedFrame parentFrame;
                if (strs.Length == 1)
                {
                    transformName = path;
                    parentFrame = RootFrame;
                }
                else
                {
                    transformName = strs.Last();
                    var parentFramePath = path.Substring(0, path.LastIndexOf('/'));
                    parentFrame = RootFrame.FindRelativeFrameWithPath(parentFramePath);
                }
                var skeletonPose = avatar.m_Avatar.m_DefaultPose;
                var xform = skeletonPose.m_X[i];
                var frame = RootFrame.FindChild(transformName);
                if (frame != null)
                {
                    SetFrame(frame, xform.t, xform.q, xform.s);
                }
                else
                {
                    frame = CreateFrame(transformName, xform.t, xform.q, xform.s);
                }
                parentFrame.AddChild(frame);
            }
        }

        private string GetPathByChannelName(string channelName)
        {
            foreach (var morph in MorphList)
            {
                foreach (var channel in morph.Channels)
                {
                    if (channel.Name == channelName)
                    {
                        return morph.Path;
                    }
                }
            }
            return null;
        }

        private string GetChannelNameFromHash(uint attribute)
        {
            if (morphChannelNames.TryGetValue(attribute, out var name))
            {
                return name;
            }
            else
            {
                return null;
            }
        }

        public record Options
        {
            public ImageFormat imageFormat;
            public TextureExportMode textureMode;
            public Game game;
            public bool collectAnimations;
            public bool exportAnimations = true;
            public bool preferBakedHumanoidBodyAnimation;
            public string humanoidBakeSolver = "AvatarPreEulerPost";
            public bool exportMaterials;
            public HashSet<Material> materials;
            public Dictionary<string, (bool, int)> uvs;
            public Dictionary<string, int> texs;
            public Func<Texture2D, string, bool> textureDataExists;
            public Func<string, IDictionary<string, object>, IDisposable> profileMeasure;
            public Dictionary<Material, ImportedMaterial> materialCache;
            public Dictionary<Texture2D, ImportedTexture> textureCache;
            public Dictionary<string, List<ImportedVertex>> meshVertexCache;
            public Queue<string> meshVertexCacheOrder;
            public int maxMeshVertexCacheEntries = 256;
            public int maxCachedMeshVertices = 200000;
            public bool useAnimatorHierarchy = true;
        }
    }
}
