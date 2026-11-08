using System.Collections.Immutable;
using System.Reflection;
using FastPsdTachiePlugin;
using YukkuriMovieMaker.Plugin.FileSource.Psd;
using YukkuriMovieMaker.Plugin.Tachie.Psd;
using YukkuriMovieMaker.Plugin.Voice;

if (args.Length > 0 && args[0] == "--dump") { Console.WriteLine(DeterminismTests.DumpMode()); return 0; }
int cacheFails = CacheTests.Run();
int detFails = DeterminismTests.Run();
int xdevFails = XDevTests.Run();
int docFails = DocStoreTests.Run();
// The rest compares against the standard ApplyAnimation (seeded by string.GetHashCode), so use the same seed mode
SeedHash.Random = true;

// Synthetic PSD tree: 5 folders x 12 layers
var root = new PsdFolder();
int id = 0;
var folders = new List<PsdFolder>();
for (int f = 0; f < 5; f++)
{
    var folder = new PsdFolder($"F{f}", id++, true);
    folders.Add(folder);
    root.AddItem(folder);
    for (int l = 0; l < 12; l++) folder.AddItem(new PsdLayer($"L{f}_{l}", id++, true));
}
var layers = new List<PsdLayer>();
void Walk(PsdFolder x) { foreach (var i in x.Items) { if (i is PsdFolder s) Walk(s); else if (i is PsdLayer pl) layers.Add(pl); } }
Walk(root);
var allIds = layers.Select(l => l.Id).ToList();
var folderIds = folders.Select(f => f.Id).ToList();

var rng = new Random(12345);

var settings = new PsdFileSettings
{
    EyeAnimations = ImmutableList.Create(
        new PsdEyeAnimation(ImmutableList.Create(allIds[0], allIds[1], allIds[2]), 0.0, 0.0),
        new PsdEyeAnimation(ImmutableList.Create(allIds[14], allIds[15]), 1.3, 4.0),
        new PsdEyeAnimation(ImmutableList.Create(allIds[30], allIds[31], allIds[32], allIds[33]), 0.0, 6.0)),
    MouthAnimations = ImmutableList.Create(
        new PsdMouthAnimation(ImmutableList.Create(allIds[3], allIds[4], allIds[5], allIds[6])),
        new PsdMouthAnimation(ImmutableList.Create(allIds[20], allIds[21]))),
    MouthVowelAnimations = ImmutableList.Create(
        new PsdVowelMouthAnimation(allIds[40], allIds[41], allIds[42], allIds[43], allIds[44], allIds[45], allIds[46]),
        new PsdVowelMouthAnimation(allIds[3], allIds[47], allIds[48], allIds[49], allIds[50], allIds[51], allIds[52])),
};

var apply = typeof(PsdTachieSource).GetMethod("ApplyAnimation", BindingFlags.Static | BindingFlags.NonPublic)!;
var pars = apply.GetParameters();
var eyeEnum = pars[6].ParameterType; var mouthEnum = pars[7].ParameterType;
Array eyeVals = Enum.GetValues(eyeEnum), mouthVals = Enum.GetValues(mouthEnum);
var shapes = Enum.GetValues<MouthShape>();

long fails = 0, trials = 0, baseFails = 0, withAnim = 0, differsFromBase = 0;
for (int t = 0; t < 4000; t++)
{
    // Base: random layers plus random folder ids
    var enable = ImmutableList.CreateRange(allIds.Where(_ => rng.Next(100) < 55).Concat(folderIds.Where(_ => rng.Next(100) < 70)).Append(allIds[0]).Distinct());
    // Standard: SetEnableItems -> GetActiveLayers
    root.SetEnableItems(enable);
    var active = root.GetActiveLayers().ToImmutableList();
    var normalized = settings.ResolveAgainst(root);
    string path = "C:\\x\\test" + (t % 3) + ".psd";

    var plan = new AnimationPlan(t + 1, root, enable, settings, path);
    if (!plan.BaseSet.SetEquals(active)) baseFails++;

    for (int k = 0; k < 60; k++)
    {
        var eyeMode = eyeVals.GetValue(rng.Next(eyeVals.Length))!;
        var mouthMode = mouthVals.GetValue(rng.Next(mouthVals.Length))!;
        var shape = shapes[rng.Next(shapes.Length)];
        double kuchi = rng.Next(8) == 0 ? -1.0 : rng.NextDouble() * 2.0;
        double sens = rng.Next(1, 300) / 10.0;
        var time = TimeSpan.FromSeconds(rng.NextDouble() * 120);

        var expected = (ImmutableList<string>)apply.Invoke(null, new object?[] { path, active, normalized, time, kuchi, sens, eyeMode, mouthMode, shape })!;

        EyeMode em = Enum.Parse<EyeMode>(eyeMode.ToString()!);
        MouthMode mm = mouthMode.ToString() switch
        {
            "VowelLipSyncPriority" => MouthMode.VowelPriority,
            "VolumeLipSyncPriority" => MouthMode.VolumePriority,
            var s => Enum.Parse<MouthMode>(s!),
        };
        var picks = plan.Pick(mm, em, kuchi, sens, shape, time);
        var mine = plan.BuildFinalSet(picks);
        trials++;
        if (plan.HasAnimation) withAnim++;
        if (!mine.SetEquals(active)) differsFromBase++;
        if (!mine.SetEquals(expected))
        {
            if (fails++ < 5) Console.WriteLine($"MISMATCH eye={eyeMode} mouth={mouthMode} shape={shape} kuchi={kuchi} sens={sens} time={time.TotalSeconds}: onlyMine=[{string.Join(",", mine.Except(expected))}] onlyStd=[{string.Join(",", expected.Except(mine))}]");
        }
    }
}
// Blink function: standard GetMabataki (non-public) and the own Blink must match bit for bit, including negative phase and boundaries
var mab = typeof(PsdTachieSource).GetMethod("GetMabataki", BindingFlags.Static | BindingFlags.NonPublic)!;
long blinkFails = 0, blinkTrials = 0;
for (int i = 0; i < 200000; i++)
{
    double offset = rng.Next(4) == 0 ? 0.0 : rng.NextDouble() * 12.0;
    double interval = 5.0 + rng.NextDouble() * 5.0;
    var tm = TimeSpan.FromSeconds(rng.NextDouble() * 1500 - (rng.Next(10) == 0 ? 20 : 0));
    double a = (double)mab.Invoke(null, new object[] { tm, offset, interval })!;
    double b = AnimationPlan.Blink(tm, offset, interval);
    blinkTrials++;
    if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b)) blinkFails++;
}
Console.WriteLine($"blink: trials={blinkTrials} mismatches={blinkFails}");
fails += blinkFails;
Console.WriteLine($"trials={trials} mismatches={fails} baseSetMismatches={baseFails} trialsWithActiveAnimation={withAnim} trialsWhereAnimationChangedTheSet={differsFromBase}");
return fails == 0 && baseFails == 0 && cacheFails == 0 && detFails == 0 && xdevFails == 0 && docFails == 0 ? 0 : 1;
