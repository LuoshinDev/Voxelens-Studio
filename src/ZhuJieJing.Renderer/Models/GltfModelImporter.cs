using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZhuJieJing.Renderer.Models;

/// <summary>Imports GLB 2.0 and glTF 2.0 documents whose buffers and images are embedded.</summary>
public static class GltfModelImporter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinaryChunkType = 0x004E4942;
    private const int MaximumContainerBytes = 512 * 1024 * 1024;
    private const int MaximumJsonBytes = 64 * 1024 * 1024;
    private const int MaximumNodeDepth = 256;

    public static ImportedModel Load(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("模型路径不能为空。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if(!info.Exists) throw new FileNotFoundException("模型文件不存在。", fullPath);
        if(info.Length <= 0 || info.Length > MaximumContainerBytes)
        {
            throw new InvalidDataException($"模型文件大小必须位于 1 到 {MaximumContainerBytes:N0} 字节之间。");
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Import(stream);
    }

    public static ImportedModel Import(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] container = ReadContainer(stream);
        var initialDiagnostics = new List<ModelImportDiagnostic>();
        (ReadOnlyMemory<byte> json, byte[]? binaryChunk) = IsGlb(container)
            ? ReadGlb(container, initialDiagnostics)
            : (TrimJsonPadding(container), null);
        if(json.Length <= 0 || json.Length > MaximumJsonBytes) throw new InvalidDataException("glTF JSON 大小无效。");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
            var context = new ImportContext(document.RootElement, binaryChunk, initialDiagnostics);
            return context.Import();
        }
        catch(JsonException exception)
        {
            throw new InvalidDataException("glTF JSON 无效。", exception);
        }
    }

    private static byte[] ReadContainer(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while(true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if(read == 0) break;
            if(output.Length + read > MaximumContainerBytes) throw new InvalidDataException("模型文件过大。");
            output.Write(buffer, 0, read);
        }
        if(output.Length == 0) throw new InvalidDataException("模型文件为空。");
        return output.ToArray();
    }

    private static bool IsGlb(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic;

    private static (ReadOnlyMemory<byte> Json, byte[]? BinaryChunk) ReadGlb(
        byte[] container,
        List<ModelImportDiagnostic> diagnostics)
    {
        ReadOnlySpan<byte> bytes = container;
        if(bytes.Length < 12) throw new InvalidDataException("GLB 文件头不完整。");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if(version != 2) throw new InvalidDataException($"仅支持 GLB 2.0，文件版本为 {version}。");
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if(declaredLength != bytes.Length) throw new InvalidDataException("GLB 文件头长度与实际长度不一致。");

        ReadOnlyMemory<byte> json = default;
        byte[]? binary = null;
        int offset = 12;
        while(offset < bytes.Length)
        {
            if(bytes.Length - offset < 8) throw new InvalidDataException("GLB Chunk 文件头不完整。");
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 0)..]);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            offset += 8;
            if(chunkLength > int.MaxValue || chunkLength > bytes.Length - offset)
            {
                throw new InvalidDataException("GLB Chunk 长度越界。");
            }
            var chunk = container.AsMemory(offset, (int)chunkLength);
            offset += (int)chunkLength;

            if(chunkType == JsonChunkType)
            {
                if(!json.IsEmpty) throw new InvalidDataException("GLB 包含多个 JSON Chunk。");
                json = TrimJsonPadding(chunk);
            }
            else if(chunkType == BinaryChunkType)
            {
                if(binary is null) binary = chunk.ToArray();
                else diagnostics.Add(new ModelImportDiagnostic(
                    ModelImportDiagnosticSeverity.Warning,
                    "GLTF_EXTRA_BINARY_CHUNK",
                    "GLB 包含额外 BIN Chunk，已忽略后续数据。"));
            }
        }

        if(json.IsEmpty) throw new InvalidDataException("GLB 缺少 JSON Chunk。");
        return (json, binary);
    }

    private static ReadOnlyMemory<byte> TrimJsonPadding(ReadOnlyMemory<byte> json)
    {
        ReadOnlySpan<byte> span = json.Span;
        int length = span.Length;
        while(length > 0 && span[length - 1] is 0 or 0x20 or 0x09 or 0x0A or 0x0D) length--;
        return json[..length];
    }

    private sealed class ImportContext
    {
        private readonly JsonElement _root;
        private readonly byte[]? _binaryChunk;
        private readonly List<ModelImportDiagnostic> _diagnostics;
        private readonly List<ImportedModelPrimitive> _primitives = [];
        private readonly List<ImportedModelTexture> _textures = [];
        private byte[]?[] _buffers = [];
        private int?[] _textureSources = [];
        private ImportedModelMaterial[] _materials = [];
        private Vector3 _minimum = new(float.PositiveInfinity);
        private Vector3 _maximum = new(float.NegativeInfinity);

        public ImportContext(JsonElement root, byte[]? binaryChunk, List<ModelImportDiagnostic> initialDiagnostics)
        {
            _root = root;
            _binaryChunk = binaryChunk;
            _diagnostics = initialDiagnostics;
        }

        public ImportedModel Import()
        {
            ValidateAsset();
            ReportUnsupportedRequiredExtensions();
            LoadBuffers();
            LoadImagesAndTextures();
            LoadMaterials();
            LoadScene();
            bool empty = _primitives.Count == 0 || !float.IsFinite(_minimum.X);
            var bounds = empty ? ImportedModelBounds.Empty : new ImportedModelBounds(_minimum, _maximum, false);
            return new ImportedModel(_primitives.ToArray(), _textures.ToArray(), _diagnostics.ToArray(), bounds);
        }

        private void ValidateAsset()
        {
            if(_root.ValueKind != JsonValueKind.Object ||
               !_root.TryGetProperty("asset", out JsonElement asset) || asset.ValueKind != JsonValueKind.Object ||
               !TryGetString(asset, "version", out string? version) ||
               !version!.StartsWith("2.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("仅支持 glTF 2.0 文件。");
            }
        }

        private void ReportUnsupportedRequiredExtensions()
        {
            if(!TryGetArray(_root, "extensionsRequired", out JsonElement extensions)) return;
            foreach(JsonElement extension in extensions.EnumerateArray())
            {
                if(extension.ValueKind != JsonValueKind.String) continue;
                _diagnostics.Add(new ModelImportDiagnostic(
                    ModelImportDiagnosticSeverity.Warning,
                    "GLTF_REQUIRED_EXTENSION_UNSUPPORTED",
                    $"未实现必需扩展 {extension.GetString()}；依赖它的 Primitive 将被跳过。"));
            }
        }

        private void LoadBuffers()
        {
            if(!TryGetArray(_root, "buffers", out JsonElement buffers)) return;
            _buffers = new byte[]?[buffers.GetArrayLength()];
            for(int index = 0; index < _buffers.Length; index++)
            {
                JsonElement buffer = buffers[index];
                int expectedLength = GetRequiredNonNegativeInt32(buffer, "byteLength", $"buffers[{index}].byteLength");
                byte[]? bytes = null;
                if(TryGetString(buffer, "uri", out string? uri))
                {
                    if(!TryDecodeDataUri(uri!, out bytes, out _))
                    {
                        AddError("GLTF_EXTERNAL_BUFFER_UNSUPPORTED", $"buffers[{index}] 使用外部 URI；仅支持内嵌 data: URI。 ");
                    }
                }
                else if(index == 0 && _binaryChunk is not null)
                {
                    bytes = _binaryChunk;
                }
                else
                {
                    AddError("GLTF_BUFFER_MISSING", $"buffers[{index}] 没有可用的内嵌数据。");
                }

                if(bytes is not null && bytes.Length < expectedLength)
                {
                    AddError("GLTF_BUFFER_TOO_SHORT", $"buffers[{index}] 声明 {expectedLength} 字节，实际只有 {bytes.Length} 字节。");
                    bytes = null;
                }
                _buffers[index] = bytes;
            }
        }

        private void LoadImagesAndTextures()
        {
            int?[] imageToImportedTexture;
            if(TryGetArray(_root, "images", out JsonElement images))
            {
                imageToImportedTexture = new int?[images.GetArrayLength()];
                for(int index = 0; index < imageToImportedTexture.Length; index++)
                {
                    try
                    {
                        byte[]? bytes = ReadImageBytes(images[index], index);
                        if(bytes is null) continue;
                        ImportedModelTexture decoded = DecodeImage(bytes);
                        imageToImportedTexture[index] = _textures.Count;
                        _textures.Add(decoded);
                    }
                    catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException or FileFormatException or OverflowException)
                    {
                        AddError("GLTF_IMAGE_DECODE_FAILED", $"images[{index}] 无法解码：{exception.Message}");
                    }
                }
            }
            else
            {
                imageToImportedTexture = [];
            }

            if(!TryGetArray(_root, "textures", out JsonElement textures)) return;
            _textureSources = new int?[textures.GetArrayLength()];
            for(int index = 0; index < _textureSources.Length; index++)
            {
                JsonElement texture = textures[index];
                if(!TryGetInt32(texture, "source", out int source) || source < 0 || source >= imageToImportedTexture.Length)
                {
                    AddError("GLTF_TEXTURE_SOURCE_INVALID", $"textures[{index}] 没有有效的内嵌图像 source。");
                    continue;
                }
                _textureSources[index] = imageToImportedTexture[source];
            }
        }

        private byte[]? ReadImageBytes(JsonElement image, int imageIndex)
        {
            if(TryGetString(image, "uri", out string? uri))
            {
                if(TryDecodeDataUri(uri!, out byte[]? bytes, out _)) return bytes;
                AddError("GLTF_EXTERNAL_IMAGE_UNSUPPORTED", $"images[{imageIndex}] 使用外部 URI；仅支持内嵌 data: URI。");
                return null;
            }
            if(TryGetInt32(image, "bufferView", out int viewIndex)) return ReadBufferViewBytes(viewIndex);
            AddError("GLTF_IMAGE_MISSING", $"images[{imageIndex}] 没有 URI 或 bufferView。");
            return null;
        }

        private void LoadMaterials()
        {
            if(!TryGetArray(_root, "materials", out JsonElement materials)) return;
            _materials = new ImportedModelMaterial[materials.GetArrayLength()];
            for(int index = 0; index < _materials.Length; index++)
            {
                JsonElement material = materials[index];
                Vector4 factor = Vector4.One;
                int? textureIndex = null;
                if(TryGetObject(material, "pbrMetallicRoughness", out JsonElement pbr))
                {
                    if(TryGetArray(pbr, "baseColorFactor", out JsonElement factorArray) && factorArray.GetArrayLength() == 4)
                    {
                        factor = new Vector4(
                            ReadFiniteSingle(factorArray[0], "baseColorFactor[0]"),
                            ReadFiniteSingle(factorArray[1], "baseColorFactor[1]"),
                            ReadFiniteSingle(factorArray[2], "baseColorFactor[2]"),
                            ReadFiniteSingle(factorArray[3], "baseColorFactor[3]"));
                    }
                    if(TryGetObject(pbr, "baseColorTexture", out JsonElement baseColorTexture))
                    {
                        int texCoord = TryGetInt32(baseColorTexture, "texCoord", out int specifiedTexCoord) ? specifiedTexCoord : 0;
                        if(texCoord != 0)
                        {
                            _diagnostics.Add(new ModelImportDiagnostic(
                                ModelImportDiagnosticSeverity.Warning,
                                "GLTF_TEXCOORD_SET_UNSUPPORTED",
                                $"materials[{index}] 使用 TEXCOORD_{texCoord}，已忽略该纹理。"));
                        }
                        else if(TryGetInt32(baseColorTexture, "index", out int gltfTextureIndex) &&
                                gltfTextureIndex >= 0 && gltfTextureIndex < _textureSources.Length)
                        {
                            textureIndex = _textureSources[gltfTextureIndex];
                            if(textureIndex is null)
                            {
                                AddError("GLTF_BASE_COLOR_TEXTURE_UNAVAILABLE", $"materials[{index}] 的 baseColorTexture 无法读取。");
                            }
                        }
                        else
                        {
                            AddError("GLTF_BASE_COLOR_TEXTURE_INVALID", $"materials[{index}] 的 baseColorTexture 索引无效。");
                        }
                    }
                }

                ImportedModelAlphaMode alphaMode = TryGetString(material, "alphaMode", out string? alphaName)
                    ? alphaName switch
                    {
                        "MASK" => ImportedModelAlphaMode.Mask,
                        "BLEND" => ImportedModelAlphaMode.Blend,
                        _ => ImportedModelAlphaMode.Opaque,
                    }
                    : ImportedModelAlphaMode.Opaque;
                float alphaCutoff = TryGetProperty(material, "alphaCutoff", out JsonElement cutoff)
                    ? ReadFiniteSingle(cutoff, $"materials[{index}].alphaCutoff")
                    : 0.5f;
                bool doubleSided = TryGetBoolean(material, "doubleSided", out bool specifiedDoubleSided) && specifiedDoubleSided;
                _materials[index] = new ImportedModelMaterial(factor, textureIndex, alphaMode, alphaCutoff, doubleSided);
            }
        }

        private void LoadScene()
        {
            if(!TryGetArray(_root, "nodes", out JsonElement nodes))
            {
                LoadUninstancedMeshes();
                return;
            }

            int[] roots = ResolveSceneRoots(nodes);
            var activePath = new HashSet<int>();
            foreach(int nodeIndex in roots) TraverseNode(nodes, nodeIndex, Matrix4x4.Identity, activePath, 0);
        }

        private int[] ResolveSceneRoots(JsonElement nodes)
        {
            if(TryGetArray(_root, "scenes", out JsonElement scenes) && scenes.GetArrayLength() > 0)
            {
                int sceneIndex = TryGetInt32(_root, "scene", out int requested) ? requested : 0;
                if(sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength())
                {
                    AddError("GLTF_SCENE_INDEX_INVALID", $"默认 scene 索引 {sceneIndex} 越界，已使用 scenes[0]。");
                    sceneIndex = 0;
                }
                return ReadIndexArray(scenes[sceneIndex], "nodes", "GLTF_SCENE_NODE_INVALID");
            }

            var children = new HashSet<int>();
            foreach(JsonElement node in nodes.EnumerateArray())
            {
                foreach(int child in ReadIndexArray(node, "children", "GLTF_NODE_CHILD_INVALID")) children.Add(child);
            }
            int[] inferred = Enumerable.Range(0, nodes.GetArrayLength()).Where(index => !children.Contains(index)).ToArray();
            return inferred.Length > 0 ? inferred : Enumerable.Range(0, nodes.GetArrayLength()).ToArray();
        }

        private void TraverseNode(
            JsonElement nodes,
            int nodeIndex,
            Matrix4x4 parentWorld,
            HashSet<int> activePath,
            int depth)
        {
            if(nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
            {
                AddError("GLTF_NODE_INDEX_INVALID", $"节点索引 {nodeIndex} 越界。", nodeIndex);
                return;
            }
            if(depth > MaximumNodeDepth)
            {
                AddError("GLTF_NODE_DEPTH_EXCEEDED", $"节点层级超过 {MaximumNodeDepth}。", nodeIndex);
                return;
            }
            if(!activePath.Add(nodeIndex))
            {
                AddError("GLTF_NODE_CYCLE", $"节点 {nodeIndex} 构成循环引用，已停止该分支。", nodeIndex);
                return;
            }

            JsonElement node = nodes[nodeIndex];
            Matrix4x4 world = ReadNodeTransform(node, nodeIndex) * parentWorld;
            if(TryGetInt32(node, "mesh", out int meshIndex)) LoadMesh(meshIndex, nodeIndex, world);
            foreach(int childIndex in ReadIndexArray(node, "children", "GLTF_NODE_CHILD_INVALID"))
            {
                TraverseNode(nodes, childIndex, world, activePath, depth + 1);
            }
            activePath.Remove(nodeIndex);
        }

        private void LoadUninstancedMeshes()
        {
            if(!TryGetArray(_root, "meshes", out JsonElement meshes)) return;
            _diagnostics.Add(new ModelImportDiagnostic(
                ModelImportDiagnosticSeverity.Warning,
                "GLTF_NODES_MISSING",
                "glTF 没有 nodes；已用单位变换载入所有 mesh。"));
            for(int meshIndex = 0; meshIndex < meshes.GetArrayLength(); meshIndex++)
            {
                LoadMesh(meshIndex, -1, Matrix4x4.Identity);
            }
        }

        private void LoadMesh(int meshIndex, int nodeIndex, Matrix4x4 world)
        {
            if(!TryGetArray(_root, "meshes", out JsonElement meshes) || meshIndex < 0 || meshIndex >= meshes.GetArrayLength())
            {
                AddError("GLTF_MESH_INDEX_INVALID", $"节点引用的 mesh 索引 {meshIndex} 越界。", nodeIndex, meshIndex);
                return;
            }
            JsonElement mesh = meshes[meshIndex];
            if(!TryGetArray(mesh, "primitives", out JsonElement primitives))
            {
                AddError("GLTF_MESH_PRIMITIVES_MISSING", $"meshes[{meshIndex}] 没有 primitives。", nodeIndex, meshIndex);
                return;
            }

            int primitiveIndex = 0;
            foreach(JsonElement primitive in primitives.EnumerateArray())
            {
                try
                {
                    LoadPrimitive(primitive, nodeIndex, meshIndex, primitiveIndex, world);
                }
                catch(PrimitiveImportException exception)
                {
                    AddError(exception.Code, exception.Message, nodeIndex, meshIndex, primitiveIndex);
                }
                catch(Exception exception) when(exception is InvalidDataException or OverflowException or ArgumentException)
                {
                    AddError("GLTF_PRIMITIVE_INVALID", exception.Message, nodeIndex, meshIndex, primitiveIndex);
                }
                primitiveIndex++;
            }
        }

        private void LoadPrimitive(
            JsonElement primitive,
            int nodeIndex,
            int meshIndex,
            int primitiveIndex,
            Matrix4x4 world)
        {
            int mode = TryGetInt32(primitive, "mode", out int specifiedMode) ? specifiedMode : 4;
            if(mode != 4)
            {
                throw new PrimitiveImportException(
                    "GLTF_PRIMITIVE_MODE_UNSUPPORTED",
                    $"Primitive mode {mode} 不受支持；当前仅支持 TRIANGLES (4)。");
            }
            if(TryGetObject(primitive, "extensions", out JsonElement extensions) &&
               extensions.TryGetProperty("KHR_draco_mesh_compression", out _))
            {
                throw new PrimitiveImportException("GLTF_DRACO_UNSUPPORTED", "Draco 压缩 Primitive 暂不支持。");
            }
            if(!TryGetObject(primitive, "attributes", out JsonElement attributes) ||
               !TryGetInt32(attributes, "POSITION", out int positionAccessor))
            {
                throw new PrimitiveImportException("GLTF_POSITION_MISSING", "Primitive 缺少 POSITION 属性。");
            }

            Vector3[] positions = ReadVector3Accessor(positionAccessor, "POSITION", requireFloat: true);
            uint[] indices = TryGetInt32(primitive, "indices", out int indexAccessor)
                ? ReadIndexAccessor(indexAccessor)
                : Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray();
            if(indices.Length == 0 || indices.Length % 3 != 0)
            {
                throw new PrimitiveImportException("GLTF_TRIANGLE_INDEX_COUNT_INVALID", "TRIANGLES 的索引数量必须是 3 的倍数且不能为零。");
            }
            if(indices.Any(index => index >= positions.Length))
            {
                throw new PrimitiveImportException("GLTF_INDEX_OUT_OF_RANGE", "Primitive 索引超出 POSITION 顶点范围。");
            }

            Vector3[]? normals = null;
            if(TryGetInt32(attributes, "NORMAL", out int normalAccessor))
            {
                normals = ReadVector3Accessor(normalAccessor, "NORMAL", requireFloat: true);
                if(normals.Length != positions.Length)
                {
                    throw new PrimitiveImportException("GLTF_NORMAL_COUNT_MISMATCH", "NORMAL 与 POSITION 的顶点数量不一致。");
                }
            }
            Vector2[] textureCoordinates = new Vector2[positions.Length];
            if(TryGetInt32(attributes, "TEXCOORD_0", out int textureAccessor))
            {
                textureCoordinates = ReadVector2Accessor(textureAccessor, "TEXCOORD_0");
                if(textureCoordinates.Length != positions.Length)
                {
                    throw new PrimitiveImportException("GLTF_TEXCOORD_COUNT_MISMATCH", "TEXCOORD_0 与 POSITION 的顶点数量不一致。");
                }
            }

            var transformedPositions = new Vector3[positions.Length];
            for(int index = 0; index < positions.Length; index++)
            {
                transformedPositions[index] = Vector3.Transform(positions[index], world);
                if(!IsFinite(transformedPositions[index]))
                {
                    throw new PrimitiveImportException("GLTF_TRANSFORM_NONFINITE", "节点变换生成了非有限顶点坐标。");
                }
            }

            float determinant = world.GetDeterminant();
            if(float.IsFinite(determinant) && determinant < 0f)
            {
                for(int index = 0; index < indices.Length; index += 3)
                {
                    (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
                }
            }

            Vector3[] transformedNormals;
            if(normals is not null && Matrix4x4.Invert(world, out Matrix4x4 inverseWorld))
            {
                Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverseWorld);
                transformedNormals = new Vector3[normals.Length];
                for(int index = 0; index < normals.Length; index++)
                {
                    Vector3 normal = Vector3.TransformNormal(normals[index], normalMatrix);
                    transformedNormals[index] = NormalizeOrDefault(normal);
                }
            }
            else
            {
                if(normals is not null)
                {
                    _diagnostics.Add(new ModelImportDiagnostic(
                        ModelImportDiagnosticSeverity.Warning,
                        "GLTF_NORMAL_TRANSFORM_SINGULAR",
                        "节点变换不可逆，已根据三角形重新生成法线。",
                        nodeIndex,
                        meshIndex,
                        primitiveIndex));
                }
                transformedNormals = GenerateNormals(transformedPositions, indices);
            }

            ImportedModelMaterial material = ImportedModelMaterial.Default;
            if(TryGetInt32(primitive, "material", out int materialIndex))
            {
                if(materialIndex < 0 || materialIndex >= _materials.Length)
                {
                    throw new PrimitiveImportException("GLTF_MATERIAL_INDEX_INVALID", $"材质索引 {materialIndex} 越界。");
                }
                material = _materials[materialIndex];
            }

            var vertices = new ImportedModelVertex[positions.Length];
            for(int index = 0; index < vertices.Length; index++)
            {
                vertices[index] = new ImportedModelVertex(
                    transformedPositions[index],
                    transformedNormals[index],
                    textureCoordinates[index]);
                _minimum = Vector3.Min(_minimum, transformedPositions[index]);
                _maximum = Vector3.Max(_maximum, transformedPositions[index]);
            }
            _primitives.Add(new ImportedModelPrimitive(vertices, indices, material, nodeIndex, meshIndex, primitiveIndex));
        }

        private Vector3[] ReadVector3Accessor(int accessorIndex, string semantic, bool requireFloat)
        {
            AccessorView view = ReadAccessor(accessorIndex, "VEC3", semantic);
            if(requireFloat && view.ComponentType != 5126)
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_COMPONENT_UNSUPPORTED", $"{semantic} 只支持 FLOAT componentType。");
            }
            var result = new Vector3[view.Count];
            for(int index = 0; index < result.Length; index++)
            {
                result[index] = new Vector3(
                    view.ReadComponent(index, 0),
                    view.ReadComponent(index, 1),
                    view.ReadComponent(index, 2));
            }
            return result;
        }

        private Vector2[] ReadVector2Accessor(int accessorIndex, string semantic)
        {
            AccessorView view = ReadAccessor(accessorIndex, "VEC2", semantic);
            if(view.ComponentType is not (5121 or 5123 or 5126))
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_COMPONENT_UNSUPPORTED", $"{semantic} 只支持 UNSIGNED_BYTE、UNSIGNED_SHORT 或 FLOAT。");
            }
            if(view.ComponentType != 5126 && !view.Normalized)
            {
                throw new PrimitiveImportException("GLTF_TEXCOORD_NOT_NORMALIZED", $"{semantic} 的整数数据必须设置 normalized=true。");
            }
            var result = new Vector2[view.Count];
            for(int index = 0; index < result.Length; index++)
            {
                result[index] = new Vector2(view.ReadComponent(index, 0), view.ReadComponent(index, 1));
            }
            return result;
        }

        private uint[] ReadIndexAccessor(int accessorIndex)
        {
            AccessorView view = ReadAccessor(accessorIndex, "SCALAR", "indices");
            if(view.ComponentType is not (5121 or 5123 or 5125))
            {
                throw new PrimitiveImportException("GLTF_INDEX_COMPONENT_UNSUPPORTED", "indices 只支持 UNSIGNED_BYTE、UNSIGNED_SHORT 或 UNSIGNED_INT。");
            }
            var result = new uint[view.Count];
            for(int index = 0; index < result.Length; index++) result[index] = view.ReadUnsigned(index);
            return result;
        }

        private AccessorView ReadAccessor(int accessorIndex, string expectedType, string semantic)
        {
            if(!TryGetArray(_root, "accessors", out JsonElement accessors) ||
               accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_INDEX_INVALID", $"{semantic} accessor 索引 {accessorIndex} 越界。");
            }
            JsonElement accessor = accessors[accessorIndex];
            if(accessor.TryGetProperty("sparse", out _))
            {
                throw new PrimitiveImportException("GLTF_SPARSE_ACCESSOR_UNSUPPORTED", $"{semantic} 使用 sparse accessor，暂不支持。");
            }
            if(!TryGetString(accessor, "type", out string? type) || !string.Equals(type, expectedType, StringComparison.Ordinal))
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_TYPE_INVALID", $"{semantic} accessor 必须是 {expectedType}。");
            }
            if(!TryGetInt32(accessor, "componentType", out int componentType))
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_COMPONENT_MISSING", $"{semantic} accessor 缺少 componentType。");
            }
            int count = GetRequiredNonNegativeInt32(accessor, "count", $"{semantic}.count");
            if(count == 0) throw new PrimitiveImportException("GLTF_ACCESSOR_EMPTY", $"{semantic} accessor 为空。");
            if(!TryGetInt32(accessor, "bufferView", out int viewIndex))
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_BUFFER_VIEW_MISSING", $"{semantic} accessor 没有 bufferView。");
            }
            bool normalized = TryGetBoolean(accessor, "normalized", out bool specifiedNormalized) && specifiedNormalized;
            int accessorOffset = TryGetInt32(accessor, "byteOffset", out int specifiedOffset) ? specifiedOffset : 0;
            return CreateAccessorView(viewIndex, accessorOffset, count, componentType, ComponentsForType(expectedType), normalized, semantic);
        }

        private AccessorView CreateAccessorView(
            int viewIndex,
            int accessorOffset,
            int count,
            int componentType,
            int componentCount,
            bool normalized,
            string semantic)
        {
            if(!TryGetArray(_root, "bufferViews", out JsonElement views) || viewIndex < 0 || viewIndex >= views.GetArrayLength())
            {
                throw new PrimitiveImportException("GLTF_BUFFER_VIEW_INDEX_INVALID", $"{semantic} bufferView 索引 {viewIndex} 越界。");
            }
            JsonElement view = views[viewIndex];
            if(view.TryGetProperty("extensions", out JsonElement extensions) && extensions.ValueKind == JsonValueKind.Object)
            {
                throw new PrimitiveImportException("GLTF_COMPRESSED_BUFFER_VIEW_UNSUPPORTED", $"{semantic} 使用了未支持的 bufferView 扩展。");
            }
            if(!TryGetInt32(view, "buffer", out int bufferIndex) || bufferIndex < 0 || bufferIndex >= _buffers.Length || _buffers[bufferIndex] is null)
            {
                throw new PrimitiveImportException("GLTF_BUFFER_UNAVAILABLE", $"{semantic} 引用的 buffer 不可用。");
            }
            int viewOffset = TryGetInt32(view, "byteOffset", out int specifiedViewOffset) ? specifiedViewOffset : 0;
            int viewLength = GetRequiredNonNegativeInt32(view, "byteLength", $"bufferViews[{viewIndex}].byteLength");
            int componentSize = ComponentSize(componentType);
            int elementSize = checked(componentSize * componentCount);
            int stride = TryGetInt32(view, "byteStride", out int specifiedStride) ? specifiedStride : elementSize;
            if(accessorOffset < 0 || viewOffset < 0 || stride < elementSize)
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_LAYOUT_INVALID", $"{semantic} accessor 的偏移或 stride 无效。");
            }
            long lastByte = (long)accessorOffset + (long)(count - 1) * stride + elementSize;
            if(lastByte > viewLength)
            {
                throw new PrimitiveImportException("GLTF_ACCESSOR_OUT_OF_RANGE", $"{semantic} accessor 超出 bufferView 范围。");
            }
            byte[] buffer = _buffers[bufferIndex]!;
            long absoluteEnd = (long)viewOffset + viewLength;
            if(absoluteEnd > buffer.Length)
            {
                throw new PrimitiveImportException("GLTF_BUFFER_VIEW_OUT_OF_RANGE", $"{semantic} bufferView 超出 buffer 范围。");
            }
            return new AccessorView(buffer, checked(viewOffset + accessorOffset), stride, count, componentType, normalized);
        }

        private byte[] ReadBufferViewBytes(int viewIndex)
        {
            if(!TryGetArray(_root, "bufferViews", out JsonElement views) || viewIndex < 0 || viewIndex >= views.GetArrayLength())
            {
                throw new InvalidDataException($"bufferView 索引 {viewIndex} 越界。");
            }
            JsonElement view = views[viewIndex];
            if(!TryGetInt32(view, "buffer", out int bufferIndex) || bufferIndex < 0 || bufferIndex >= _buffers.Length || _buffers[bufferIndex] is null)
            {
                throw new InvalidDataException($"bufferViews[{viewIndex}] 引用的 buffer 不可用。");
            }
            int offset = TryGetInt32(view, "byteOffset", out int specifiedOffset) ? specifiedOffset : 0;
            int length = GetRequiredNonNegativeInt32(view, "byteLength", $"bufferViews[{viewIndex}].byteLength");
            byte[] source = _buffers[bufferIndex]!;
            if(offset < 0 || (long)offset + length > source.Length)
            {
                throw new InvalidDataException($"bufferViews[{viewIndex}] 超出 buffer 范围。");
            }
            return source.AsSpan(offset, length).ToArray();
        }

        private Matrix4x4 ReadNodeTransform(JsonElement node, int nodeIndex)
        {
            if(TryGetArray(node, "matrix", out JsonElement matrix))
            {
                if(matrix.GetArrayLength() != 16)
                {
                    AddError("GLTF_NODE_MATRIX_INVALID", $"nodes[{nodeIndex}].matrix 必须有 16 个数。", nodeIndex);
                    return Matrix4x4.Identity;
                }
                float[] values = matrix.EnumerateArray().Select((value, index) => ReadFiniteSingle(value, $"nodes[{nodeIndex}].matrix[{index}]")).ToArray();
                return new Matrix4x4(
                    values[0], values[1], values[2], values[3],
                    values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11],
                    values[12], values[13], values[14], values[15]);
            }

            Vector3 translation = ReadVector3(node, "translation", Vector3.Zero, nodeIndex);
            Vector3 scale = ReadVector3(node, "scale", Vector3.One, nodeIndex);
            Quaternion rotation = ReadQuaternion(node, nodeIndex);
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        }

        private Vector3 ReadVector3(JsonElement owner, string propertyName, Vector3 fallback, int nodeIndex)
        {
            if(!TryGetArray(owner, propertyName, out JsonElement values)) return fallback;
            if(values.GetArrayLength() != 3)
            {
                AddError("GLTF_NODE_TRS_INVALID", $"nodes[{nodeIndex}].{propertyName} 必须有 3 个数。", nodeIndex);
                return fallback;
            }
            return new Vector3(
                ReadFiniteSingle(values[0], propertyName),
                ReadFiniteSingle(values[1], propertyName),
                ReadFiniteSingle(values[2], propertyName));
        }

        private Quaternion ReadQuaternion(JsonElement node, int nodeIndex)
        {
            if(!TryGetArray(node, "rotation", out JsonElement values)) return Quaternion.Identity;
            if(values.GetArrayLength() != 4)
            {
                AddError("GLTF_NODE_ROTATION_INVALID", $"nodes[{nodeIndex}].rotation 必须有 4 个数。", nodeIndex);
                return Quaternion.Identity;
            }
            var rotation = new Quaternion(
                ReadFiniteSingle(values[0], "rotation"),
                ReadFiniteSingle(values[1], "rotation"),
                ReadFiniteSingle(values[2], "rotation"),
                ReadFiniteSingle(values[3], "rotation"));
            return rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
        }

        private int[] ReadIndexArray(JsonElement owner, string propertyName, string diagnosticCode)
        {
            if(!TryGetArray(owner, propertyName, out JsonElement values)) return [];
            var result = new List<int>(values.GetArrayLength());
            foreach(JsonElement value in values.EnumerateArray())
            {
                if(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index)) result.Add(index);
                else AddError(diagnosticCode, $"{propertyName} 包含非整数索引。");
            }
            return result.ToArray();
        }

        private void AddError(string code, string message, int? node = null, int? mesh = null, int? primitive = null) =>
            _diagnostics.Add(new ModelImportDiagnostic(ModelImportDiagnosticSeverity.Error, code, message, node, mesh, primitive));

        private static ImportedModelTexture DecodeImage(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if(decoder.Frames.Count == 0) throw new InvalidDataException("图像不包含帧。");
            BitmapSource source = decoder.Frames[0];
            if(source.PixelWidth <= 0 || source.PixelHeight <= 0) throw new InvalidDataException("图像尺寸无效。");
            if(source.PixelWidth > 16384 || source.PixelHeight > 16384) throw new InvalidDataException("图像尺寸超过 16384。");
            if(source.Format != PixelFormats.Bgra32)
            {
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            }
            int stride = checked(source.PixelWidth * 4);
            var pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);
            bool transparent = false;
            bool translucent = false;
            for(int offset = 3; offset < pixels.Length; offset += 4)
            {
                byte alpha = pixels[offset];
                transparent |= alpha == 0;
                translucent |= alpha is > 0 and < 255;
            }
            return new ImportedModelTexture(source.PixelWidth, source.PixelHeight, pixels, transparent, translucent);
        }

        private static Vector3[] GenerateNormals(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];
            for(int index = 0; index < indices.Length; index += 3)
            {
                int a = checked((int)indices[index]);
                int b = checked((int)indices[index + 1]);
                int c = checked((int)indices[index + 2]);
                Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if(!IsFinite(face)) continue;
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }
            for(int index = 0; index < normals.Length; index++) normals[index] = NormalizeOrDefault(normals[index]);
            return normals;
        }

        private static Vector3 NormalizeOrDefault(Vector3 value) =>
            IsFinite(value) && value.LengthSquared() > 0.0000001f ? Vector3.Normalize(value) : Vector3.UnitY;

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static int ComponentsForType(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new InvalidDataException($"不支持 accessor type {type}。"),
        };

        private static int ComponentSize(int componentType) => componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => throw new PrimitiveImportException("GLTF_ACCESSOR_COMPONENT_UNSUPPORTED", $"不支持 componentType {componentType}。"),
        };

        private static int GetRequiredNonNegativeInt32(JsonElement owner, string propertyName, string label)
        {
            if(!TryGetInt32(owner, propertyName, out int value) || value < 0)
            {
                throw new InvalidDataException($"{label} 必须是非负整数。");
            }
            return value;
        }

        private static float ReadFiniteSingle(JsonElement value, string label)
        {
            if(value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) || !float.IsFinite(result))
            {
                throw new InvalidDataException($"{label} 必须是有限数字。");
            }
            return result;
        }

        private static bool TryGetProperty(JsonElement owner, string name, out JsonElement value)
        {
            value = default;
            return owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out value);
        }

        private static bool TryGetArray(JsonElement owner, string name, out JsonElement value) =>
            TryGetProperty(owner, name, out value) && value.ValueKind == JsonValueKind.Array;

        private static bool TryGetObject(JsonElement owner, string name, out JsonElement value) =>
            TryGetProperty(owner, name, out value) && value.ValueKind == JsonValueKind.Object;

        private static bool TryGetString(JsonElement owner, string name, out string? value)
        {
            value = null;
            if(!TryGetProperty(owner, name, out JsonElement property) || property.ValueKind != JsonValueKind.String) return false;
            value = property.GetString();
            return value is not null;
        }

        private static bool TryGetInt32(JsonElement owner, string name, out int value)
        {
            value = default;
            return TryGetProperty(owner, name, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
        }

        private static bool TryGetBoolean(JsonElement owner, string name, out bool value)
        {
            value = default;
            if(!TryGetProperty(owner, name, out JsonElement property) ||
               property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            value = property.GetBoolean();
            return true;
        }

        private sealed class AccessorView
        {
            private readonly byte[] _buffer;
            private readonly int _offset;
            private readonly int _stride;

            public AccessorView(byte[] buffer, int offset, int stride, int count, int componentType, bool normalized)
            {
                _buffer = buffer;
                _offset = offset;
                _stride = stride;
                Count = count;
                ComponentType = componentType;
                Normalized = normalized;
            }

            public int Count { get; }

            public int ComponentType { get; }

            public bool Normalized { get; }

            public float ReadComponent(int index, int component)
            {
                int position = checked(_offset + index * _stride + component * ComponentSize(ComponentType));
                ReadOnlySpan<byte> bytes = _buffer.AsSpan(position);
                return ComponentType switch
                {
                    5120 => Normalized ? Math.Max((sbyte)bytes[0] / 127f, -1f) : (sbyte)bytes[0],
                    5121 => Normalized ? bytes[0] / 255f : bytes[0],
                    5122 => Normalized
                        ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f, -1f)
                        : BinaryPrimitives.ReadInt16LittleEndian(bytes),
                    5123 => Normalized
                        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f
                        : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    5125 => Normalized
                        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) / 4294967295f
                        : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                    _ => throw new InvalidDataException($"不支持 componentType {ComponentType}。"),
                };
            }

            public uint ReadUnsigned(int index)
            {
                int position = checked(_offset + index * _stride);
                ReadOnlySpan<byte> bytes = _buffer.AsSpan(position);
                return ComponentType switch
                {
                    5121 => bytes[0],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    _ => throw new InvalidDataException($"不支持 index componentType {ComponentType}。"),
                };
            }
        }
    }

    private sealed class PrimitiveImportException : Exception
    {
        public PrimitiveImportException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private static bool TryDecodeDataUri(string uri, out byte[]? bytes, out string? mediaType)
    {
        bytes = null;
        mediaType = null;
        if(!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        int comma = uri.IndexOf(',');
        if(comma < 5) throw new InvalidDataException("data: URI 缺少数据分隔符。");
        string metadata = uri[5..comma];
        string payload = uri[(comma + 1)..];
        string[] parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        mediaType = parts.FirstOrDefault(part => !string.Equals(part, "base64", StringComparison.OrdinalIgnoreCase));
        bool base64 = parts.Any(part => string.Equals(part, "base64", StringComparison.OrdinalIgnoreCase));
        try
        {
            bytes = base64 ? Convert.FromBase64String(payload) : PercentDecode(payload);
            return true;
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException("data: URI 编码无效。", exception);
        }
    }

    private static byte[] PercentDecode(string payload)
    {
        using var output = new MemoryStream(payload.Length);
        for(int index = 0; index < payload.Length; index++)
        {
            char character = payload[index];
            if(character == '%' && index + 2 < payload.Length &&
               byte.TryParse(payload.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte decoded))
            {
                output.WriteByte(decoded);
                index += 2;
            }
            else if(character <= byte.MaxValue)
            {
                output.WriteByte((byte)character);
            }
            else
            {
                throw new InvalidDataException("非 base64 data: URI 包含非字节字符。");
            }
        }
        return output.ToArray();
    }
}
