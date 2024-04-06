﻿namespace OpenShock.ShockOsc.Ui.Utils;

public static class AsynchronousEventExtensions
{
    public static Task Raise(this Func<Task>? handlers)
    {
        if (handlers != null)
        {
            return Task.WhenAll(handlers.GetInvocationList()
                .OfType<Func<Task>>()
                .Select(h => h()));
        }

        return Task.CompletedTask;
    }

}