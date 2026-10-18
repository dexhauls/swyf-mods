using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes humanoid (muscle) clips onto the Mixamo skeleton exactly the way Unity plays them, and writes the
/// Emoty dance file. Run (Unity must be signed in via Unity Hub):
///   Unity.exe -batchmode -quit -projectPath ModSource/Emotes/tools/UnityBake -executeMethod DanceBaker.Bake
///             -rig "path/to/mixamo.fbx" -clips "folder with .anim" -out "path/to/dances.bin"
/// </summary>
public static class DanceBaker
{
    private const string RigAsset = "Assets/Rig/rig.fbx";
    private const string ClipFolder = "Assets/Clips";

    public static void Bake()
    {
        string rig = Arg("-rig"), clips = Arg("-clips"), output = Arg("-out");
        Directory.CreateDirectory("Assets/Rig");
        Directory.CreateDirectory(ClipFolder);
        File.Copy(rig, RigAsset, true);
        Directory.Delete(ClipFolder, true);
        Directory.CreateDirectory(ClipFolder);
        // Either flat "<Name>.anim" files, or one folder per dance with start.anim (optional, played once) + loop.anim.
        var jobs = new List<(string name, string start, string loop)>();
        foreach (var f in Directory.GetFiles(clips, "*.anim"))
        {
            string dst = Path.Combine(ClipFolder, Path.GetFileName(f));
            File.Copy(f, dst, true);
            jobs.Add((null, null, dst));
        }
        int n = 0;
        foreach (var dir in Directory.GetDirectories(clips))
        {
            string loop = Path.Combine(dir, "loop.anim");
            if (!File.Exists(loop)) continue;
            string dst = Path.Combine(ClipFolder, "d" + (n++));
            Directory.CreateDirectory(dst);
            File.Copy(loop, Path.Combine(dst, "loop.anim"), true);
            string start = Path.Combine(dir, "start.anim");
            if (File.Exists(start)) File.Copy(start, Path.Combine(dst, "start.anim"), true);
            jobs.Add((Path.GetFileName(dir), File.Exists(start) ? Path.Combine(dst, "start.anim") : null, Path.Combine(dst, "loop.anim")));
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        // Humanoid import, avatar from this model (Mixamo bones map automatically).
        var importer = (ModelImporter)AssetImporter.GetAtPath(RigAsset);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.SaveAndReimport();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigAsset);
        var go = Object.Instantiate(prefab);
        var animator = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
        animator.avatar = AssetDatabase.LoadAllAssetsAtPath(RigAsset).OfType<Avatar>().FirstOrDefault();
        animator.applyRootMotion = false;
        if (animator.avatar == null || !animator.avatar.isHuman) { Debug.LogError("DanceBaker: no humanoid avatar"); EditorApplication.Exit(2); return; }

        var bones = go.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("mixamorig:")).ToList();
        var hips = bones.First(t => t.name == "mixamorig:Hips");
        var leg = bones.First(t => t.name == "mixamorig:LeftLeg");

        var dances = new List<byte[]>();
        foreach (var job in jobs)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(job.loop.Replace('\\', '/'));
            var startClip = job.start != null ? AssetDatabase.LoadAssetAtPath<AnimationClip>(job.start.Replace('\\', '/')) : null;
            if (clip == null) { Debug.LogWarning("DanceBaker: could not load " + job.loop); continue; }
            float fps = clip.frameRate > 0 ? clip.frameRate : 30f;
            int introFrames = startClip != null ? Mathf.Max(1, Mathf.RoundToInt(startClip.length * fps)) : 0;
            int frames = introFrames + Mathf.Max(1, Mathf.RoundToInt(clip.length * fps) + 1);
            var rot = bones.Select(_ => new Quaternion[frames]).ToArray();
            var offs = new Vector3[frames];
            Vector3 hips0 = Vector3.zero;

            AnimationMode.StartAnimationMode();
            for (int f = 0; f < frames; f++)
            {
                bool intro = f < introFrames;
                var c = intro ? startClip : clip;
                float time = intro ? f / fps : (f - introFrames) / fps;
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(go, c, Mathf.Min(time, c.length));
                AnimationMode.EndSampling();
                for (int b = 0; b < bones.Count; b++)
                {
                    var q = bones[b].localRotation;
                    if (f > 0 && Quaternion.Dot(q, rot[b][f - 1]) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    rot[b][f] = q;
                }
                if (f == 0) hips0 = hips.localPosition;
                offs[f] = hips.localPosition - hips0;
            }
            AnimationMode.StopAnimationMode();

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            string name = job.name ?? clip.name;
            Str(w, "swyf-dance:" + new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()));
            Str(w, job.name ?? Pretty(name));
            w.Write(fps);
            w.Write((uint)frames);
            w.Write(leg.localPosition.magnitude);
            w.Write(1u);
            w.Write((uint)introFrames); // version 2: loop starts after the intro
            w.Write((uint)bones.Count);
            for (int b = 0; b < bones.Count; b++)
            {
                Str(w, bones[b].name);
                foreach (var q in rot[b]) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
            }
            foreach (var o in offs) { w.Write(o.x); w.Write(o.y); w.Write(o.z); }
            dances.Add(ms.ToArray());
            Debug.Log($"DanceBaker: {name} {frames} frames");
        }

        using (var w = new BinaryWriter(File.Create(output)))
        {
            w.Write(Encoding.ASCII.GetBytes("SWYD"));
            w.Write(2u);
            w.Write((uint)dances.Count);
            foreach (var d in dances) w.Write(d);
        }
        Debug.Log($"DanceBaker: wrote {dances.Count} dances to {output}");
        Object.DestroyImmediate(go);
    }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    private static string Pretty(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || name[i - 1] == '\'')) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString().Replace("'T", "'t");
    }

    private static string Arg(string key)
    {
        var a = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(a, key);
        if (i < 0 || i + 1 >= a.Length) throw new System.ArgumentException("missing " + key);
        return a[i + 1];
    }
}
