using LiCvWriter.Application.Models;
using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class FoundryRuntimeFailureClassifierTests
{
    [Fact]
    public void TryCreateException_TensorRtDeserializeMessage_ReturnsRetriableLoadFailure()
    {
        var exception = new InvalidOperationException(
            "Error executing load_model: Microsoft.ML.OnnxRuntimeGenAI.OnnxRuntimeGenAIException: NvTensorRTRTX EP failed to deserialize engine for fused node: NvTensorRTRTXExecutionProvider_TRTKernel_graph_main_graph_784800302306871645_0_0");

        var classified = FoundryRuntimeFailureClassifier.TryCreateException(
            exception,
            retryAttempted: true,
            modelCacheRoot: @"C:\Users\henri\.LI-CV-Writer\cache\models",
            logsRoot: @"C:\Users\henri\.LI-CV-Writer\logs");

        Assert.NotNull(classified);
        Assert.Equal(FoundryRuntimeFailureKind.TensorRtEngineLoad, classified!.FailureKind);
        Assert.Equal("Foundry TensorRT engine load failed after a runtime reset retry.", classified.Message);
        Assert.True(FoundryRuntimeFailureClassifier.IsRetriableModelLoadFailure(exception));
        Assert.Contains(classified.Notes, static note => note.Contains("resetting the in-process Foundry runtime", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(classified.Notes, static note => note.Contains(@"C:\Users\henri\.LI-CV-Writer\cache\models", StringComparison.Ordinal));
    }

    [Fact]
    public void TryCreateException_CudaOutOfMemoryMessage_ReturnsGpuOutOfMemoryFailure()
    {
        var exception = new InvalidOperationException(
            "Error executing chat_completions: Microsoft.ML.OnnxRuntimeGenAI.OnnxRuntimeGenAIException: CUDA failure 2: out of memory ; GPU=0 ; file=nv_allocator.cc");

        var classified = FoundryRuntimeFailureClassifier.TryCreateException(
            exception,
            retryAttempted: false,
            logsRoot: @"C:\Users\henri\.LI-CV-Writer\logs");

        Assert.NotNull(classified);
        Assert.Equal(FoundryRuntimeFailureKind.TensorRtGpuOutOfMemory, classified!.FailureKind);
        Assert.Equal("Foundry TensorRT execution ran out of GPU memory.", classified.Message);
        Assert.False(FoundryRuntimeFailureClassifier.IsRetriableModelLoadFailure(exception));
        Assert.Contains(classified.Notes, static note => note.Contains("Free VRAM", StringComparison.OrdinalIgnoreCase));
    }
}