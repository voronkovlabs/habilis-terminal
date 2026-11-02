using System.Net;
using Amazon.Runtime;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Common;

/// <summary>Turns AWS failures into plain-language messages that say what the user can do (no stack traces, no raw responses).</summary>
public static class AwsErrors
{
    /// <summary>A <see cref="PluginException"/> for <paramref name="error"/>; <paramref name="action"/> says what was being done.</summary>
    public static PluginException Describe(Exception error, string action, AwsSession session)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(session);
        if (error is PluginException plugin)
        {
            return plugin;
        }

        var message = error switch
        {
            AmazonServiceException service => DescribeService(service, session),
            HttpRequestException => $"Cannot reach AWS ({session.Region}). Check the network connection and proxy settings.",
            TaskCanceledException or TimeoutException => $"AWS did not answer within {session.TimeoutSeconds} seconds. Try again, or raise the request timeout in the settings.",
            AmazonClientException client when client.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) || client.Message.Contains("token", StringComparison.OrdinalIgnoreCase) =>
                $"The credentials of profile \"{session.Profile}\" could not be used: {FirstLine(client.Message)} If it is an SSO profile, run: aws sso login --profile {session.Profile}",
            AmazonClientException client => FirstLine(client.Message),
            _ => FirstLine(error.Message),
        };
        return new PluginException($"{action} failed. {message}", error);
    }

    private static string DescribeService(AmazonServiceException error, AwsSession session)
    {
        var code = error.ErrorCode ?? string.Empty;
        return code switch
        {
            "ExpiredToken" or "ExpiredTokenException" or "RequestExpired" or "TokenRefreshRequired" =>
                $"The credentials of profile \"{session.Profile}\" have expired. Refresh them (for SSO: aws sso login --profile {session.Profile}) and press Ctrl+R.",
            "InvalidClientTokenId" or "UnrecognizedClientException" or "SignatureDoesNotMatch" or "InvalidAccessKeyId" =>
                $"AWS rejected the credentials of profile \"{session.Profile}\". Check the access keys in the AWS credentials file.",
            "AccessDenied" or "AccessDeniedException" or "UnauthorizedOperation" or "AuthorizationError" or "Forbidden" =>
                $"Permission missing: {MissingPermission(error)}. Ask for this permission on the profile's role or user.",
            "Throttling" or "ThrottlingException" or "TooManyRequestsException" or "RequestLimitExceeded" or "SlowDown" =>
                "AWS is throttling the requests. Wait a moment and try again.",
            _ when error.StatusCode == HttpStatusCode.Forbidden => $"Permission missing: {MissingPermission(error)}.",
            _ => $"{FirstLine(error.Message)} ({code})",
        };
    }

    /// <summary>The action named in an access-denied message ("... is not authorized to perform: sqs:ListQueues ..."), if any.</summary>
    internal static string MissingPermission(AmazonServiceException error)
    {
        const string marker = "perform: ";
        var message = error.Message ?? string.Empty;
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return FirstLine(message);
        }

        var action = message[(start + marker.Length)..];
        var end = action.IndexOfAny([' ', ',', '.']);
        return end > 0 ? action[..end] : action;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
