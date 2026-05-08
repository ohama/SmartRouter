module SmartRouter.Core.MLPorts

open System.Threading
open System.Threading.Tasks

/// Embedding port — takes a prompt, returns 1024-dim L2-normalized float32 vector.
/// Adapter (BgeM3Embedder, in Cli) implements this against OnnxRuntime + SentencePiece.
/// Pure interface: no NuGet imports, no Microsoft.ML reference.
type IEmbedder =
    abstract member EmbedAsync :
        prompt : string * ct : CancellationToken
        -> Task<float32[]>

/// Classifier prediction result.
/// Score: sigmoid probability in [0..1]; >= MlThreshold → Qwen122B.
/// PredictedLabel: ML.NET LR convention (true = positive class = Qwen122B).
[<Struct>]
type ClassifierPrediction =
    { Score          : float32
      PredictedLabel : bool }

/// Classifier port — takes 1024-dim embedding, returns prediction.
/// Adapter (MlNetClassifier, in Cli) implements this against PredictionEnginePool.
type IClassifier =
    abstract member PredictAsync :
        embedding : float32[] * ct : CancellationToken
        -> Task<ClassifierPrediction>
