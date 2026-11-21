using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoingMultiplayer
{
    internal enum ActorRole { Local, Remote, LocalCoop }

    internal sealed class NetworkActor
    {
        internal int PlayerId;
        internal ActorRole Role;
        internal LocalSeats.Seat Seat;
        internal RemotePuppet Puppet;
        internal Vector3 LastPosition;
        internal float LastYaw;
        internal float LastHealth;
        internal byte Animation;
        internal bool Persistent;
    }

    // Maps a session player id to a Unity actor. Local actors are owned by this
    // process; remote actors are display puppets with no authoritative movement.
    internal static class ActorRegistry
    {
        private static readonly Dictionary<int, NetworkActor> actors =
            new Dictionary<int, NetworkActor>();

        internal static int Count { get { return actors.Count; } }

        internal static IEnumerable<NetworkActor> All { get { return actors.Values; } }

        internal static void Reset()
        {
            LocalSeats.Clear();
            actors.Clear();
        }

        internal static NetworkActor AddRemote(Player local, int playerId, Vector3 position)
        {
            if (playerId <= 0) throw new ArgumentOutOfRangeException("playerId");
            if (actors.ContainsKey(playerId)) throw new InvalidOperationException("Duplicate actor id");
            LocalSeats.Seat seat = LocalSeats.Spawn(local, position, true, true);
            seat.Network = new NetworkInputState();
            var actor = new NetworkActor
            {
                PlayerId = playerId,
                Role = ActorRole.Remote,
                Seat = seat,
                Persistent = true,
                LastPosition = position,
                LastHealth = seat.Player.health,
            };
            PreparePuppet(actor);
            actors[playerId] = actor;
            return actor;
        }

        internal static NetworkActor Get(int playerId)
        {
            NetworkActor actor;
            return actors.TryGetValue(playerId, out actor) ? actor : null;
        }

        internal static void Remove(int playerId)
        {
            NetworkActor actor;
            if (!actors.TryGetValue(playerId, out actor)) return;
            LocalSeats.Forget(actor.Seat);
            if (actor.Seat != null && actor.Seat.Player)
                UnityEngine.Object.Destroy(actor.Seat.Player.gameObject);
            if (actor.Seat != null && actor.Seat.Data)
                UnityEngine.Object.Destroy(actor.Seat.Data.gameObject);
            if (actor.Seat != null && actor.Seat.Material)
                UnityEngine.Object.Destroy(actor.Seat.Material);
            if (actor.Seat != null && actor.Seat.Costume)
                UnityEngine.Object.Destroy(actor.Seat.Costume);
            actors.Remove(playerId);
        }

        internal static PlayerSnapshot Capture(Player player)
        {
            var snapshot = new PlayerSnapshot
            {
                X = player.transform.position.x,
                Y = player.transform.position.y,
                Z = player.transform.position.z,
                Yaw = player.transform.eulerAngles.y,
                Health = player.health,
                Animation = 0,
            };
            if (player.rb)
            {
                Vector3 velocity = player.rb.velocity;
                velocity.y = 0f;
                snapshot.Animation = velocity.magnitude > 0.25f ? (byte)1 : (byte)0;
            }
            return snapshot;
        }

        internal static void ApplySnapshot(int playerId, PlayerSnapshot snapshot)
        {
            NetworkActor actor;
            if (!actors.TryGetValue(playerId, out actor)) return;
            actor.LastPosition = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
            actor.LastYaw = snapshot.Yaw;
            actor.LastHealth = snapshot.Health;
            actor.Animation = snapshot.Animation;
            if (actor.Puppet != null)
                actor.Puppet.Push(actor.LastPosition, snapshot.Yaw, snapshot.Health, snapshot.Animation);
            else if (actor.Seat != null && actor.Seat.Player)
                actor.Seat.Player.transform.position = actor.LastPosition;
        }

        // Called after a scene transition and after the new scene is ready. The
        // data holder and persistent player should have survived; if Unity or the
        // game destroyed the player, recreate only the scene object.
        internal static void RebindAll(Vector3 fallback)
        {
            foreach (NetworkActor actor in actors.Values)
            {
                if (actor.Seat == null) continue;
                if (!actor.Seat.Player)
                    LocalSeats.Respawn(actor.Seat, fallback);
                if (!actor.Seat.Player) continue;
                Vector3 target = actor.LastPosition == Vector3.zero ? fallback : actor.LastPosition;
                actor.Seat.Player.gameObject.SetActive(true);
                actor.Seat.Player.transform.position = target;
                if (actor.Seat.Player.rb)
                {
                    actor.Seat.Player.rb.position = target;
                    actor.Seat.Player.rb.velocity = Vector3.zero;
                }
                PreparePuppet(actor);
            }
        }

        private static void PreparePuppet(NetworkActor actor)
        {
            Player player = actor.Seat.Player;
            player.LockMovement(true);
            player.SetPhysicalCollidersActive(false);
            if (player.rb) player.rb.isKinematic = true;
            RemotePuppet puppet = player.GetComponent<RemotePuppet>();
            if (puppet == null) puppet = player.gameObject.AddComponent<RemotePuppet>();
            puppet.Initialize(player);
            actor.Puppet = puppet;
        }
    }

    // Interpolates a remote actor between the last two host snapshots. The
    // puppet never runs gameplay movement or owns authoritative state.
    internal sealed class RemotePuppet : MonoBehaviour
    {
        private struct Sample
        {
            internal float Time;
            internal Vector3 Position;
            internal float Yaw;
            internal float Health;
            internal byte Animation;
        }

        private const float InterpolationDelay = 0.1f;
        private readonly List<Sample> samples = new List<Sample>();
        private Player player;
        private Vector3 lastApplied;

        internal Vector3 LastApplied { get { return lastApplied; } }
        internal int Pushed;
        internal int Updated;

        internal void Initialize(Player value)
        {
            player = value;
            lastApplied = value.transform.position;
        }

        internal void Push(Vector3 position, float yaw, float health, byte animation)
        {
            Pushed++;
            samples.Add(new Sample
            {
                Time = Time.realtimeSinceStartup,
                Position = position,
                Yaw = yaw,
                Health = health,
                Animation = animation,
            });
            while (samples.Count > 32) samples.RemoveAt(0);
        }

        private void Update()
        {
            if (player == null || !player) return;
            Updated++;
            if (samples.Count == 0) return;
            float renderTime = Time.realtimeSinceStartup - InterpolationDelay;
            Sample from = samples[0];
            Sample to = samples[0];
            if (samples.Count > 1)
            {
                for (int i = 1; i < samples.Count; i++)
                {
                    if (samples[i].Time < renderTime)
                    {
                        from = samples[i];
                        to = samples[i];
                        continue;
                    }
                    to = samples[i];
                    break;
                }
            }
            float span = to.Time - from.Time;
            float t = span <= 0.0001f ? 1f : Mathf.Clamp01((renderTime - from.Time) / span);
            Vector3 position = Vector3.Lerp(from.Position, to.Position, t);
            float yaw = Mathf.LerpAngle(from.Yaw, to.Yaw, t);
            player.transform.position = position;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (player.rb)
            {
                player.rb.position = position;
                player.rb.velocity = Vector3.zero;
            }
            lastApplied = position;
        }
    }
}
