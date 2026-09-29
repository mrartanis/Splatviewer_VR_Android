// SPDX-License-Identifier: MIT
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Stores one explicitly approved TLS certificate fingerprint per server origin.</summary>
public static class VRPhotoCertificate
{
    public static string Fingerprint(byte[] certificateBytes)
    {
        using (var certificate = new X509Certificate2(certificateBytes))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-", "");
    }

    static string Key(string origin)
    {
        using (var sha = SHA256.Create())
            return "vrphoto-cert-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(origin))).Replace("-", "");
    }

    public static string Saved(string origin) => PlayerPrefs.GetString(Key(origin), "");

    public static void Save(string origin, string fingerprint)
    {
        PlayerPrefs.SetString(Key(origin), fingerprint);
        PlayerPrefs.Save();
    }
}

public sealed class VRPhotoCertificateHandler : CertificateHandler
{
    readonly string _approved;
    readonly bool _probe;
    public string Presented { get; private set; }

    public VRPhotoCertificateHandler(string approved, bool probe = false)
    {
        _approved = approved;
        _probe = probe;
    }

    protected override bool ValidateCertificate(byte[] certificateData)
    {
        try
        {
            Presented = VRPhotoCertificate.Fingerprint(certificateData);
            return _probe || (!string.IsNullOrEmpty(_approved) &&
                string.Equals(Presented, _approved, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error)
        {
            Debug.LogWarning("[VRPhoto] Invalid server certificate: " + error.Message);
            return false;
        }
    }
}
