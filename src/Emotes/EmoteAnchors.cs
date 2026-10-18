using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Emotes
{
    /// <summary>
    /// The fart and pee effects come out of fixed points on the player object, not out of the skeleton, so during
    /// an emote they stayed where the idle body would be. While a player emotes, those points ride along with the
    /// hips; afterwards they go back to where they were.
    /// </summary>
    internal static class EmoteAnchors
    {
        private sealed class Anchor
        {
            public Transform T;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public Vector3 OffsetInHips;
            public Quaternion RotInHips;
        }

        private sealed class Tracked
        {
            public PlayerEmoterNetworking Emoter;
            public Transform Hips;
            public readonly List<Anchor> Anchors = new List<Anchor>();
        }

        private static readonly List<Tracked> Active = new List<Tracked>();
        private static readonly AccessTools.FieldRef<PeeNetworking, GameObject> PeeStream =
            AccessTools.FieldRefAccess<PeeNetworking, GameObject>("goopStreamEffect");

        /// <summary>Called when an emote starts on <paramref name="emoter"/> (the body is still in its idle pose).</summary>
        public static void Begin(PlayerEmoterNetworking emoter)
        {
            End(emoter);
            var hips = ActiveHips(emoter);
            if (hips == null) return;
            var tr = new Tracked { Emoter = emoter, Hips = hips };
            var fart = emoter.GetComponentInChildren<FartPlayer>(true);
            if (fart != null) Add(tr, fart.transform);
            var pee = emoter.GetComponentInChildren<PeeNetworking>(true);
            if (pee != null && PeeStream(pee) != null) Add(tr, PeeStream(pee).transform);
            if (tr.Anchors.Count > 0) Active.Add(tr);
        }

        private static void Add(Tracked tr, Transform t)
        {
            tr.Anchors.Add(new Anchor
            {
                T = t,
                LocalPos = t.localPosition,
                LocalRot = t.localRotation,
                OffsetInHips = tr.Hips.InverseTransformPoint(t.position),
                RotInHips = Quaternion.Inverse(tr.Hips.rotation) * t.rotation,
            });
        }

        private static Transform ActiveHips(PlayerEmoterNetworking emoter)
        {
            foreach (var a in emoter.anims)
            {
                if (a == null || !a.gameObject.activeInHierarchy) continue;
                foreach (var t in a.GetComponentsInChildren<Transform>())
                    if (t.name == "mixamorig:Hips") return t;
            }
            return null;
        }

        /// <summary>After the final pose of the frame is set (animator + dance driver).</summary>
        public static void LateTick()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var tr = Active[i];
                if (tr.Emoter == null || tr.Hips == null || !tr.Emoter.IsEmoting) { Restore(tr); Active.RemoveAt(i); continue; }
                Follow(tr);
            }
        }

        private static void Follow(Tracked tr)
        {
            foreach (var a in tr.Anchors)
            {
                if (a.T == null) continue;
                a.T.position = tr.Hips.TransformPoint(a.OffsetInHips);
                a.T.rotation = tr.Hips.rotation * a.RotInHips;
            }
        }

        /// <summary>The pee stream re-aims itself every LateUpdate; aim it again along the hips.</summary>
        public static void AfterPeeAim(PeeNetworking pee)
        {
            var stream = PeeStream(pee);
            if (stream == null) return;
            foreach (var tr in Active)
                foreach (var a in tr.Anchors)
                    if (a.T == stream.transform && tr.Hips != null && tr.Emoter != null && tr.Emoter.IsEmoting)
                    {
                        a.T.position = tr.Hips.TransformPoint(a.OffsetInHips);
                        a.T.rotation = tr.Hips.rotation * a.RotInHips;
                    }
        }

        public static void End(PlayerEmoterNetworking emoter)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
                if (Active[i].Emoter == emoter) { Restore(Active[i]); Active.RemoveAt(i); }
        }

        private static void Restore(Tracked tr)
        {
            foreach (var a in tr.Anchors)
            {
                if (a.T == null) continue;
                a.T.localPosition = a.LocalPos;
                a.T.localRotation = a.LocalRot;
            }
        }

        public static void RestoreAll()
        {
            foreach (var tr in Active) Restore(tr);
            Active.Clear();
        }
    }

    [HarmonyPatch(typeof(PeeNetworking), "LateUpdate")]
    internal static class PeeAimPatch
    {
        private static void Postfix(PeeNetworking __instance) => EmoteAnchors.AfterPeeAim(__instance);
    }
}
