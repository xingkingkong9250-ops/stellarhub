using System.Text;

namespace QL.World.Tests.Support;

/// <summary>
/// Hand-crafted minimal ONNX protobuf for LocalAiService tests, avoiding any
/// binary test assets. Graph:
/// pixel_values [1,3,224,224] float → Reshape(shape_1d) → Slice(0..512)
/// → Reshape(shape_out) → image_embeds [1,512].
/// The output is therefore exactly the first 512 values of the flattened
/// preprocessed input tensor.
/// </summary>
internal static class MiniOnnx
{
    public const string InputName = "pixel_values";
    public const string OutputName = "image_embeds";

    public const string TextInputName = "input_ids";
    public const string TextOutputName = "text_embeds";

    public static byte[] BuildClipStubModel()
    {
        byte[] shape1d = Init("shape_1d", [1], 150528);
        byte[] starts = Init("starts", [1], 0);
        byte[] ends = Init("ends", [1], 512);
        byte[] axes = Init("axes", [1], 0);
        byte[] steps = Init("steps", [1], 1);
        byte[] shapeOut = Init("shape_out", [2], 1, 512);

        byte[] graph = Msg(b =>
        {
            WriteMsg(b, 1, Node("Reshape", [InputName, "shape_1d"], ["flat"]));
            WriteMsg(b, 1, Node("Slice", ["flat", "starts", "ends", "axes", "steps"], ["sliced"]));
            WriteMsg(b, 1, Node("Reshape", ["sliced", "shape_out"], [OutputName]));
            WriteMsg(b, 5, shape1d);
            WriteMsg(b, 5, starts);
            WriteMsg(b, 5, ends);
            WriteMsg(b, 5, axes);
            WriteMsg(b, 5, steps);
            WriteMsg(b, 5, shapeOut);
            WriteMsg(b, 11, ValueInfo(InputName, TensorType(elemType: 1, Shape(1, 3, 224, 224))));
            WriteMsg(b, 12, ValueInfo(OutputName, TensorType(elemType: 1, Shape(1, 512))));
        });

        byte[] opset = Msg(b =>
        {
            WriteString(b, 1, "");
            WriteVarintField(b, 2, 17);
        });

        return Msg(b =>
        {
            WriteVarintField(b, 1, 8); // ir_version
            WriteMsg(b, 7, graph);
            WriteMsg(b, 8, opset);
        });
    }

    /// <summary>
    /// Stub text model: input_ids [1,77] int64 (accepted but ignored) →
    /// text_embeds [1,512] float, equal to a fixed pattern (0.001 * index
    /// for index in 0..511). Lets LocalAiService EmbedTextAsync tests assert
    /// the returned embedding without depending on real CLIP weights.
    /// </summary>
    public static byte[] BuildClipTextStubModel()
    {
        float[] stubValues = Enumerable.Range(0, 512).Select(i => i * 0.001f).ToArray();
        byte[] constShape = Init("text_const_shape", [2], 1, 512);
        byte[] constData = InitFloat("text_const_data", [1, 512], stubValues);

        byte[] graph = Msg(b =>
        {
            // Reshape(const_data, const_shape) → text_embeds
            WriteMsg(b, 1, Node("Reshape", ["text_const_data", "text_const_shape"], [TextOutputName]));
            WriteMsg(b, 5, constShape);
            WriteMsg(b, 5, constData);
            // input_ids declared as graph input (caller must provide) but unused
            WriteMsg(b, 11, ValueInfo(TextInputName, TensorType(elemType: 7, Shape(1, 77))));
            WriteMsg(b, 12, ValueInfo(TextOutputName, TensorType(elemType: 1, Shape(1, 512))));
        });

        byte[] opset = Msg(b =>
        {
            WriteString(b, 1, "");
            WriteVarintField(b, 2, 17);
        });

        return Msg(b =>
        {
            WriteVarintField(b, 1, 8); // ir_version
            WriteMsg(b, 7, graph);
            WriteMsg(b, 8, opset);
        });
    }

    // ── ONNX message builders ──────────────────────────────────────────

    private static byte[] Node(string opType, string[] inputs, string[] outputs) =>
        Msg(b =>
        {
            foreach (string input in inputs) WriteString(b, 1, input);
            foreach (string output in outputs) WriteString(b, 2, output);
            WriteString(b, 4, opType);
        });

    private static byte[] ValueInfo(string name, byte[] tensorType) =>
        Msg(b =>
        {
            WriteString(b, 1, name);
            // ValueInfoProto.type is a full TypeProto whose tensor_type = field 1.
            WriteMsg(b, 2, Msg(tb => WriteMsg(tb, 1, tensorType)));
        });

    private static byte[] TensorType(int elemType, byte[] shape) =>
        Msg(b =>
        {
            WriteVarintField(b, 1, (ulong)elemType);
            WriteMsg(b, 2, shape);
        });

    private static byte[] Shape(params long[] dims) =>
        Msg(b =>
        {
            foreach (long dim in dims)
            {
                // Each dim is a DimensionProto submessage: dim_value is field 1
                // with wiretype 0 (varint), NOT wiretype 2 (length-delimited).
                byte[] dimMsg = Msg(m => WriteVarintField(m, 1, (ulong)dim));
                WriteMsg(b, 1, dimMsg);
            }
        });

    private static byte[] Init(string name, long[] dims, params long[] values) =>
        Msg(b =>
        {
            var packed = new List<byte>();
            foreach (long dim in dims) WriteVarint(packed, (ulong)dim);
            WriteMsg(b, 1, [.. packed]); // dims
            WriteVarintField(b, 2, 7);   // data_type = INT64
            WriteString(b, 8, name);
            WriteMsg(b, 9, Le64(values)); // raw_data
        });

    private static byte[] InitFloat(string name, long[] dims, float[] values) =>
        Msg(b =>
        {
            var packed = new List<byte>();
            foreach (long dim in dims) WriteVarint(packed, (ulong)dim);
            WriteMsg(b, 1, [.. packed]); // dims
            WriteVarintField(b, 2, 1);   // data_type = FLOAT
            WriteString(b, 8, name);
            WriteMsg(b, 9, Le32(values)); // raw_data
        });

    // ── protobuf wire format primitives ────────────────────────────────

    private static byte[] Msg(Action<List<byte>> build)
    {
        var body = new List<byte>();
        build(body);
        return [.. body];
    }

    private static void WriteTag(List<byte> b, int field, int wireType) =>
        WriteVarint(b, (uint)((field << 3) | wireType));

    private static void WriteVarint(List<byte> b, ulong value)
    {
        while (value >= 0x80)
        {
            b.Add((byte)(value | 0x80));
            value >>= 7;
        }
        b.Add((byte)value);
    }

    private static void WriteVarintField(List<byte> b, int field, ulong value)
    {
        WriteTag(b, field, 0);
        WriteVarint(b, value);
    }

    private static void WriteMsg(List<byte> b, int field, byte[] payload)
    {
        WriteTag(b, field, 2);
        WriteVarint(b, (ulong)payload.Length);
        b.AddRange(payload);
    }

    private static void WriteString(List<byte> b, int field, string value) =>
        WriteMsg(b, field, Encoding.UTF8.GetBytes(value));

    private static byte[] Le64(params long[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            ulong bits = (ulong)values[i];
            for (int j = 0; j < 8; j++)
                bytes[i * 8 + j] = (byte)(bits >> (8 * j));
        }
        return bytes;
    }

    private static byte[] Le32(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }
}
