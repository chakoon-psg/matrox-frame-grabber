using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Onnx
{
    /// <summary>
    /// A screen inspector backed by an ONNX CenterNet head, on the CPU.
    ///
    /// This is the only file in the repository that names an ONNX type. The contract, the
    /// letterbox and the decode arithmetic are all in `Infrastructure/` where they can be tested;
    /// what is left here is a session, three buffers and the gather that turns one output tensor
    /// into candidates.
    ///
    /// **The graph is checked at load, not trusted.** A model file called `192` turned out to take
    /// 256x320 - the name was three revisions stale. That check is the part of the ported code
    /// worth keeping exactly as it was, and a model whose shape does not match what this class can
    /// decode is refused with a reason rather than producing quiet nonsense.
    ///
    /// **Not thread safe.** Buffers are allocated once and reused, which is what keeps allocation
    /// off a path that runs beside acquisition.
    /// </summary>
    public sealed class OnnxScreenInspector : IScreenInspector
    {
        // Where each quantity sits in the output's last dimension.
        //
        // **Channel 0 is the heatmap - measured, not assumed.** `OnnxInspector selftest` reports
        // the range of every channel across the whole grid, and channel 0 came back entirely
        // negative (-6.506 to -1.073), which is what a heatmap looks like when it has found
        // nothing. That check does not depend on the model responding to the test picture.
        //
        // **The remaining four split by their ranges**, which is structural rather than a guess:
        // an offset head emits a fraction of a cell and a size head emits a count of cells, so
        // they differ by construction. Measured: [1] -3.839..9.814 and [2] -3.693..7.666 are wide
        // enough to be sizes; [3] -2.646..1.287 and [4] -0.704..1.567 sit around zero like
        // offsets. The first draft of this file had that the other way round.
        //
        // **[추정] The order *within* each pair is still unconfirmed** - whether [1] is height or
        // width, whether [3] is y or x. Settling it needs an image with a known ground-truth box,
        // which needs a model trained on our problem. It changes where a box is drawn; it does
        // not change what running the model costs, which is what this integration is measuring.
        // The output layout, measured rather than assumed - 2026-09-16, with
        // `OnnxInspector peek` over green rectangles of known size on a real frame.
        //
        // **The order WITHIN each pair was wrong until then, and it was wrong in a way that looks
        // right.** A box still appeared, on roughly the right thing, so nothing failed; it was
        // simply transposed. `docs/measurements/2026-09-14` had already recorded the pair order as
        // unverified - this is that note being closed.
        //
        // | green rectangle | peak cell | ch1  | ch2  | as [w,h] | as [h,w] |
        // |---|---|---|---|---|---|
        // | 120x80  | on its centre | 10.392 | 7.419 | **133x95** | 95x133 |
        // | 70x190  | col exact     |  5.354 | 6.163 | **68x79**  | 79x68  |
        //
        // Channel 1 followed the WIDTH both times (133 against 120, 68 against 70); reading it as
        // the height made the tall rectangle come out wider than it is. The offsets go the same
        // way: taking ch3 as offX put the 120x80 box at 442,296 against a true 450,300, where
        // taking it as offY put it at 459,279.
        private const int ChannelHeat = 0;
        private const int ChannelSizeW = 1;
        private const int ChannelSizeH = 2;
        private const int ChannelOffsetX = 3;
        private const int ChannelOffsetY = 4;
        private const int ChannelsExpected = 5;

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly DenseTensor<byte> _input;
        private readonly List<NamedOnnxValue> _feed;
        private readonly CenterNetDecoder _decoder;
        private readonly List<PeakCandidate> _candidates = new List<PeakCandidate>(256);
        private readonly PeakCandidate[] _kept;
        private readonly string _describe;

        private readonly int _gridRows, _gridCols;
        private readonly float _strideY, _strideX;

        public OnnxScreenInspector(string modelPath, float scoreThreshold, float iouThreshold,
                                   int maxFindings, int intraOpThreads)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentNullException(nameof(modelPath));
            if (!File.Exists(modelPath)) throw new FileNotFoundException("model not found", modelPath);

            var options = new SessionOptions();
            if (intraOpThreads > 0) options.IntraOpNumThreads = intraOpThreads;
            // Without this the pool spins between calls and holds a core at 100% while nothing is
            // being inferred. On a box whose acquisition threads want those cores, that is not
            // free.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

            _session = new InferenceSession(modelPath, options);

            KeyValuePair<string, NodeMetadata> input = _session.InputMetadata.First();
            KeyValuePair<string, NodeMetadata> output = _session.OutputMetadata.First();
            _inputName = input.Key;
            _outputName = output.Key;

            int[] inDims = input.Value.Dimensions;
            int[] outDims = output.Value.Dimensions;

            // Refuse rather than guess. Every one of these has a failure that looks like bad
            // accuracy rather than like a broken model.
            if (inDims.Length != 4 || inDims[3] != 3)
                throw new InvalidDataException(
                    $"input {_inputName} is [{Dims(inDims)}]; expected [1,H,W,3]");
            if (input.Value.ElementType != typeof(byte))
                throw new InvalidDataException(
                    $"input {_inputName} is {input.Value.ElementType.Name}; expected Byte");
            if (outDims.Length != 4 || outDims[3] != ChannelsExpected)
                throw new InvalidDataException(
                    $"output {_outputName} is [{Dims(outDims)}]; expected [1,rows,cols,{ChannelsExpected}]");

            InputHeight = inDims[1];
            InputWidth = inDims[2];
            _gridRows = outDims[1];
            _gridCols = outDims[2];

            if (InputHeight <= 0 || InputWidth <= 0 || _gridRows <= 0 || _gridCols <= 0)
                throw new InvalidDataException("the graph leaves a dimension dynamic; this decoder needs fixed ones");

            _strideY = (float)InputHeight / _gridRows;
            _strideX = (float)InputWidth / _gridCols;

            _input = new DenseTensor<byte>(new byte[InputWidth * InputHeight * 3],
                                           new[] { 1, InputHeight, InputWidth, 3 });
            _feed = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, _input) };
            _decoder = new CenterNetDecoder(scoreThreshold, iouThreshold, maxFindings);
            _kept = new PeakCandidate[Math.Max(1, maxFindings)];

            _describe = $"{Path.GetFileName(modelPath)} "
                      + $"({new FileInfo(modelPath).Length / 1048576.0:F1} MB), "
                      + $"in {InputWidth}x{InputHeight}, grid {_gridCols}x{_gridRows} "
                      + $"(stride {_strideX:0.#}), score {_decoder.ScoreThreshold:0.##}, "
                      + $"iou {_decoder.IouThreshold:0.##}, "
                      + $"threads {(intraOpThreads > 0 ? intraOpThreads.ToString(CultureInfo.InvariantCulture) : "ORT default")}";
        }

        public string Describe => _describe;
        public int InputWidth { get; }
        public int InputHeight { get; }

        /// <summary>
        /// What this inspector can say.
        ///
        /// One entry, and it is <see cref="AnomalyKind.Unknown"/>: the model in hand detects
        /// "green", which is not one of our kinds. When a model trained on our faults exists this
        /// becomes the set that model was trained on, and the label-to-kind table comes with it.
        /// </summary>
        public AnomalyKind[] Reports { get; } = { AnomalyKind.Unknown };

        /// <summary>Peaks that cleared the threshold on the last frame, before suppression.</summary>
        public int LastCandidates { get; private set; }

        public int Inspect(byte[] bgr, Span<ScreenFinding> into)
        {
            if (bgr == null) throw new ArgumentNullException(nameof(bgr));
            int need = InputWidth * InputHeight * 3;
            if (bgr.Length < need)
                throw new ArgumentException($"need {need} bytes, got {bgr.Length}", nameof(bgr));

            bgr.AsSpan(0, need).CopyTo(_input.Buffer.Span);

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(_feed);
            Tensor<float> outTensor = results.First(r => r.Name == _outputName).AsTensor<float>();
            ReadOnlySpan<float> flat = ((DenseTensor<float>)outTensor).Buffer.Span;

            _candidates.Clear();
            float cut = _decoder.LogitThreshold;
            int stride = ChannelsExpected;

            for (int row = 0; row < _gridRows; row++)
            {
                int rowBase = row * _gridCols * stride;
                for (int col = 0; col < _gridCols; col++)
                {
                    int c = rowBase + col * stride;
                    float heat = flat[c + ChannelHeat];
                    if (heat < cut) continue;      // the threshold is applied in logit space

                    float cy = (row + flat[c + ChannelOffsetY]) * _strideY;
                    float cx = (col + flat[c + ChannelOffsetX]) * _strideX;
                    float h = flat[c + ChannelSizeH] * _strideY;
                    float w = flat[c + ChannelSizeW] * _strideX;

                    _candidates.Add(new PeakCandidate(row, col, 0, heat,
                                                      cx - w * 0.5f, cy - h * 0.5f, w, h));
                }
            }

            LastCandidates = _candidates.Count;
            int n = _decoder.Suppress(_candidates, _kept);

            int written = Math.Min(n, into.Length);
            for (int i = 0; i < written; i++)
            {
                PeakCandidate p = _kept[i];
                into[i] = new ScreenFinding(AnomalyKind.Unknown, "green", p.Score,
                                            p.X, p.Y, p.Width, p.Height);
            }
            return written;
        }

        private static string Dims(int[] d) =>
            string.Join(",", d.Select(x => x < 0 ? "?" : x.ToString(CultureInfo.InvariantCulture)));

        public void Dispose() => _session?.Dispose();
    }
}
