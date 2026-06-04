using System.Collections.Concurrent;
using System.Reflection;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundrySdkTaskInvoker
{
    private static readonly ConcurrentDictionary<MethodCacheKey, TaskMethodResolution> taskMethodCache = new();
    private static readonly ConcurrentDictionary<MethodCacheKey, MethodResolution> publicInstanceMethodCache = new();

    public static MethodInfo? GetOptionalPublicInstanceMethod(object target, string methodName)
        => publicInstanceMethodCache.GetOrAdd(
            new MethodCacheKey(target.GetType(), methodName),
            static key => new MethodResolution(key.TargetType.GetMethod(key.MethodName, BindingFlags.Instance | BindingFlags.Public)))
            .Method;

    public static async Task InvokeOptionalAsync(object target, string methodName, CancellationToken cancellationToken)
    {
        var invocation = ResolveTaskInvocation(target, methodName, cancellationToken);
        if (invocation is null)
        {
            return;
        }

        await InvokeResolvedAsync(target, invocation, methodName);
    }

    public static async Task InvokeRequiredAsync(object target, string methodName, CancellationToken cancellationToken)
    {
        var invocation = ResolveTaskInvocation(target, methodName, cancellationToken)
            ?? throw new InvalidOperationException($"This Foundry SDK build does not expose '{methodName}'.");

        await InvokeResolvedAsync(target, invocation, methodName);
    }

    private static async Task InvokeResolvedAsync(object target, TaskInvocation invocation, string methodName)
    {
        try
        {
            if (invocation.Method.Invoke(target, invocation.Arguments) is not Task task)
            {
                throw new InvalidOperationException($"Foundry SDK method '{methodName}' did not return a Task.");
            }

            await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static TaskInvocation? ResolveTaskInvocation(object target, string methodName, CancellationToken cancellationToken)
    {
        var resolution = taskMethodCache.GetOrAdd(
            new MethodCacheKey(target.GetType(), methodName),
            static key => ResolveTaskMethod(key.TargetType, key.MethodName));

        return resolution.ParameterKind switch
        {
            TaskMethodParameterKind.None when resolution.Method is not null => new TaskInvocation(resolution.Method, []),
            TaskMethodParameterKind.CancellationToken when resolution.Method is not null => new TaskInvocation(resolution.Method, [cancellationToken]),
            TaskMethodParameterKind.NullableCancellationToken when resolution.Method is not null => new TaskInvocation(resolution.Method, [(CancellationToken?)cancellationToken]),
            _ => null
        };
    }

    private static TaskMethodResolution ResolveTaskMethod(Type targetType, string methodName)
    {
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return new TaskMethodResolution(method, TaskMethodParameterKind.None);
            }

            if (parameters.Length == 1 && TryGetCancellationTokenParameterKind(parameters[0].ParameterType, out var parameterKind))
            {
                return new TaskMethodResolution(method, parameterKind);
            }
        }

        return TaskMethodResolution.Missing;
    }

    private static bool TryGetCancellationTokenParameterKind(Type parameterType, out TaskMethodParameterKind parameterKind)
    {
        if (parameterType == typeof(CancellationToken))
        {
            parameterKind = TaskMethodParameterKind.CancellationToken;
            return true;
        }

        if (parameterType == typeof(CancellationToken?))
        {
            parameterKind = TaskMethodParameterKind.NullableCancellationToken;
            return true;
        }

        parameterKind = TaskMethodParameterKind.Missing;
        return false;
    }

    private readonly record struct MethodCacheKey(Type TargetType, string MethodName);

    private sealed record MethodResolution(MethodInfo? Method);

    private sealed record TaskMethodResolution(MethodInfo? Method, TaskMethodParameterKind ParameterKind)
    {
        public static TaskMethodResolution Missing { get; } = new(null, TaskMethodParameterKind.Missing);
    }

    private enum TaskMethodParameterKind
    {
        Missing,
        None,
        CancellationToken,
        NullableCancellationToken
    }

    private sealed record TaskInvocation(MethodInfo Method, object?[] Arguments);
}