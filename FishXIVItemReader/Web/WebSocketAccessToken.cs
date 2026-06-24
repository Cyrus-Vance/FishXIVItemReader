using System;
using System.Collections.Generic;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FishXIVItemReader.Web
{
    public static class WebSocketAccessToken
    {
        public static string Generate()
        {
            var fingerprint = BuildHardwareFingerprint();
            var randomBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            using (var sha256 = SHA256.Create())
            {
                var material = "FishXIVItemReader.WebSocketAccessToken.v1|" +
                               fingerprint +
                               "|" +
                               Convert.ToBase64String(randomBytes);
                return ToBase64Url(sha256.ComputeHash(Encoding.UTF8.GetBytes(material)));
            }
        }

        public static bool IsUsable(string token)
        {
            return !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= 32;
        }

        private static string BuildHardwareFingerprint()
        {
            var parts = new List<string>();
            AddWmiValue(parts, "Win32_Processor", "ProcessorId");
            AddWmiValue(parts, "Win32_BaseBoard", "SerialNumber");
            AddWmiValue(parts, "Win32_BaseBoard", "Product");
            AddWmiValue(parts, "Win32_BIOS", "SerialNumber");
            AddMachineGuid(parts);

            return parts.Count == 0
                ? Environment.MachineName
                : string.Join("|", parts.ToArray());
        }

        private static void AddWmiValue(List<string> parts, string className, string propertyName)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                           "SELECT " + propertyName + " FROM " + className))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            var value = NormalizeHardwareValue(Convert.ToString(item[propertyName]));
                            if (!string.IsNullOrEmpty(value))
                            {
                                parts.Add(className + "." + propertyName + "=" + value);
                                return;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void AddMachineGuid(List<string> parts)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key == null)
                    {
                        return;
                    }

                    var value = NormalizeHardwareValue(Convert.ToString(key.GetValue("MachineGuid")));
                    if (!string.IsNullOrEmpty(value))
                    {
                        parts.Add("MachineGuid=" + value);
                    }
                }
            }
            catch
            {
            }
        }

        private static string NormalizeHardwareValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            if (string.Equals(normalized, "To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Default string", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return normalized.ToUpperInvariant();
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
