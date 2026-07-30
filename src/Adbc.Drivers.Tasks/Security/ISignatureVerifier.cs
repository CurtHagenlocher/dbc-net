using System;

namespace Adbc.Drivers.Build.Security
{
    internal enum SignatureVerificationStatus
    {
        /// <summary>Verification is not enabled by policy.</summary>
        NotAttempted,

        /// <summary>Verification is enabled but the archive carries no signature.</summary>
        NotPresent,

        /// <summary>The signature verified against a pinned key.</summary>
        Verified,

        /// <summary>The signature did not verify. Always fatal.</summary>
        Failed,
    }

    internal sealed class SignatureVerificationResult
    {
        private SignatureVerificationResult(SignatureVerificationStatus status, string? detail, string? keyFingerprint)
        {
            Status = status;
            Detail = detail;
            KeyFingerprint = keyFingerprint;
        }

        public SignatureVerificationStatus Status { get; }

        public string? Detail { get; }

        /// <summary>Fingerprint of the key that verified the signature, when known.</summary>
        public string? KeyFingerprint { get; }

        public static SignatureVerificationResult NotAttempted { get; } =
            new SignatureVerificationResult(SignatureVerificationStatus.NotAttempted, null, null);

        public static SignatureVerificationResult NotPresent { get; } =
            new SignatureVerificationResult(SignatureVerificationStatus.NotPresent, null, null);

        public static SignatureVerificationResult Verified(string keyFingerprint) =>
            new SignatureVerificationResult(SignatureVerificationStatus.Verified, null, keyFingerprint);

        public static SignatureVerificationResult Failed(string detail) =>
            new SignatureVerificationResult(SignatureVerificationStatus.Failed, detail, null);
    }

    /// <summary>
    /// Verifies a driver's detached signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated behind an interface because signature verification and content hashing
    /// answer different questions, and this repository currently only answers the second
    /// one. A SHA-256 recorded in the lock file gives reproducibility: every later build
    /// gets the same bytes the lock was reviewed against. It does not give authenticity
    /// for the <em>first</em> download, because the hash was learned from that download.
    /// </para>
    /// <para>
    /// Closing that gap needs OpenPGP verification against a pinned key fingerprint,
    /// which is deliberately a later phase. Until then <see cref="NullSignatureVerifier"/>
    /// reports <see cref="SignatureVerificationStatus.NotAttempted"/> and the receipt
    /// records that honestly, rather than implying a check that did not happen.
    /// </para>
    /// </remarks>
    internal interface ISignatureVerifier
    {
        SignatureVerificationResult Verify(string driverPath, string? signaturePath);
    }

    /// <summary>
    /// Performs no signature verification and says so.
    /// </summary>
    internal sealed class NullSignatureVerifier : ISignatureVerifier
    {
        public static NullSignatureVerifier Instance { get; } = new NullSignatureVerifier();

        public SignatureVerificationResult Verify(string driverPath, string? signaturePath) =>
            SignatureVerificationResult.NotAttempted;
    }
}
