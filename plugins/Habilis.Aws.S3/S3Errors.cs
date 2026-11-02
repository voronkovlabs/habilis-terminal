using System.Net;
using Amazon.Runtime;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

/// <summary>S3-specific plain-language errors on top of <see cref="AwsErrors"/>.</summary>
internal static class S3Errors
{
    /// <summary>
    /// A <see cref="PluginException"/> for <paramref name="error"/>. <paramref name="permission"/> names the IAM
    /// permission the call needs, for S3's bare "Access Denied" that does not say which one is missing.
    /// </summary>
    public static PluginException Describe(Exception error, string action, AwsSession session, string permission)
    {
        if (error is AmazonServiceException service)
        {
            var message = service.ErrorCode switch
            {
                "InvalidObjectState" =>
                    "The object is archived (S3 Glacier Flexible Retrieval or Deep Archive) and must be restored before it can be downloaded. " +
                    "This plugin does not restore archived objects: restore it in the AWS console or with the AWS CLI, then try again.",
                "PreconditionFailed" => "The object changed in S3 after it was listed. Refresh the panel (Ctrl+R) and try again.",
                "NoSuchBucket" => "The bucket does not exist (or no longer exists).",
                "NoSuchKey" => "The object no longer exists. Refresh the panel (Ctrl+R).",
                "NoSuchUpload" => "S3 no longer knows this multipart upload; it may have been aborted. Try again.",
                "EntityTooLarge" => "The file is larger than S3 allows for one object (5 TB).",
                "AccessDenied" or "Forbidden" or "" or null when service.StatusCode == HttpStatusCode.Forbidden && !NamesPermission(service) =>
                    $"Permission missing: {permission}. Ask for this permission on the profile's role or user.",
                _ => null,
            };
            if (message is not null)
            {
                return new PluginException($"{action} failed. {message}", error);
            }
        }

        return AwsErrors.Describe(error, action, session);
    }

    private static bool NamesPermission(AmazonServiceException error) =>
        error.Message?.Contains("perform: ", StringComparison.OrdinalIgnoreCase) == true;
}
