using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Models.Db;

/// <summary>
/// The secret tokens in the sensor URLs: 7 hex characters derived from the sensor id, a dash and a
/// random GUID, lower-case (the v0.12 format, which the sensor URLs out there carry).
/// </summary>
public static class SensorEntityTokens
{
    public static void SetNewSecretToken(this SensorEntity sensorEntity)
    {
        sensorEntity.SecretWriteToken = NewToken(sensorEntity.Id);
        sensorEntity.SecretReadToken = NewToken(sensorEntity.Id, offset: 3);
    }

    public static void SetNewWriteToken(this SensorEntity sensorEntity) =>
        sensorEntity.SecretWriteToken = NewToken(sensorEntity.Id);

    public static void SetRandomSecretReadToken(this SensorEntity sensorEntity) =>
        sensorEntity.SecretReadToken = NewToken(sensorEntity.Id, offset: 3);

    private static string NewToken(Guid seedId, int offset = 0)
    {
        var prefix = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(seedId.ToString())));
        return (prefix.Substring(offset, 7) + "-" + Guid.NewGuid()).ToLowerInvariant();
    }
}
