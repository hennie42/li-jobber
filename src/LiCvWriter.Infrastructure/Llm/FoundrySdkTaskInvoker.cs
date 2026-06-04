using System.Reflection;

namespace LiCvWriter.Infrastructure.Foundry;

internal static class FoundrySdkTaskInvoker
{
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
        foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return new TaskInvocation(method, []);
            }

            if (parameters.Length == 1 && TryBuildCancellationTokenArgument(parameters[0].ParameterType, cancellationToken, out var argument))
            {
                return new TaskInvocation(method, [argument]);
            }
        }

        return null;
    }

    private static bool TryBuildCancellationTokenArgument(Type parameterType, CancellationToken cancellationToken, out object? argument)
    {
        if (parameterType == typeof(CancellationToken))
        {
            argument = cancellationToken;
            return true;
        }

        if (parameterType == typeof(CancellationToken?))
        {
            argument = (CancellationToken?)cancellationToken;
            return true;
        }

        argument = null;
        return false;
    }

    private sealed record TaskInvocation(MethodInfo Method, object?[] Arguments);
}