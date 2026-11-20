using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GoingMultiplayer
{
    internal static class RoomFixture
    {
        // Compare the actual stationary collision fixture, not just scene/floor
        // numbers or a seed. Exclude entities/rigidbodies and trigger volumes.
        internal static string Fingerprint(Room room)
        {
            var rows = new List<string>();
            foreach (Collider collider in room.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || collider.attachedRigidbody
                    || collider.GetComponentInParent<Entity>()) continue;
                Bounds bounds = collider.bounds;
                Mesh meshData = collider is MeshCollider ? ((MeshCollider)collider).sharedMesh : null;
                string mesh = meshData ? meshData.name + ":" + meshData.vertexCount : "-";
                rows.Add(collider.GetType().Name + ":" + collider.gameObject.layer
                    + ":" + mesh + Vector(bounds.min) + Vector(bounds.max)
                    + Vector(collider.transform.eulerAngles) + Vector(collider.transform.lossyScale));
            }
            rows.Sort(System.StringComparer.Ordinal);
            if (rows.Count == 0) throw new System.InvalidOperationException("No static collision fixture");
            string text = Vector(room.minBound) + Vector(room.maxBound) + "|" + string.Join("|", rows.ToArray());
            using (var hash = SHA256.Create())
                return System.BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static string Vector(Vector3 v)
        {
            return ":" + v.x.ToString("F2", CultureInfo.InvariantCulture)
                + "," + v.y.ToString("F2", CultureInfo.InvariantCulture)
                + "," + v.z.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
