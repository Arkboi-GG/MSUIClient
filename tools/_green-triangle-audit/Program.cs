using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.World.Doodads;

const string path = @"WORLD\GENERIC\PASSIVEDOODADS\PARTICLEEMITTERS\ASHENVALEWISPS.M2";
string data = Path.GetFullPath(args.Length > 0 ? args[0] : @"GameData\Data");
var mpq = new MpqMount(data);
AdtTerrainReader.StormLibExtractor = mpq.ReadFile;
var hit = mpq.ReadFileWithSupplier(path) ?? throw new Exception("asset missing");
var m = M2Reader.Parse(hit.Data) ?? throw new Exception("parse failed");
Check(DoodadBillboardLaw.RequiresPerInstancePose(m),
    "real AshenvaleWisps billboard-card signature was not routed per instance");
M2Vertex changed = m.Vertices[0];
byte originalWeight = changed.BoneWeight0;
changed.BoneWeight0 = 254;
m.Vertices[0] = changed;
Check(DoodadBillboardLaw.RequiresPerInstancePose(m),
    "weighted billboard card stopped requiring a per-instance pose");
changed.BoneWeight0 = originalWeight;
m.Vertices[0] = changed;
Console.WriteLine("doodad billboard routing law checks passed");
Console.WriteLine($"source={hit.Supplier} bytes={hit.Data.Length} name={m.Name} version={m.Version}");
Console.WriteLine($"verts={m.Vertices.Count} indices={m.Indices.Count} submeshes={m.Submeshes.Count} batches={m.Batches.Count} textures={m.Textures.Count} lookup={m.TextureLookup.Count} flags={m.RenderFlags.Count} bones={m.Bones.Count} seq={m.Sequences.Count} emitters={m.ParticleEmitters.Count} ribbons={m.RibbonEmitters.Count}");
for (int i = 0; i < m.Textures.Count; i++)
{
    string texture = m.Textures[i].Filename;
    var d = AdtTerrainReader.ReadBlpPixels(data, texture);
    if (d is null) { Console.WriteLine($"tex[{i}] type={m.Textures[i].Type} flags=0x{m.Textures[i].Flags:X} {texture} MISSING"); continue; }
    var (px,w,h) = d.Value;
    int minA=255,maxA=0,nonzero=0,opaque=0; long sr=0,sg=0,sb=0;
    var colors = new HashSet<uint>();
    for (int p=0;p+3<px.Length;p+=4) { int b=px[p],g=px[p+1],r=px[p+2],a=px[p+3]; minA=Math.Min(minA,a);maxA=Math.Max(maxA,a);if(a>0)nonzero++;if(a==255)opaque++;sr+=r;sg+=g;sb+=b;if(colors.Count<10000)colors.Add((uint)(r|(g<<8)|(b<<16)|(a<<24))); }
    int n=Math.Max(1,px.Length/4);
    Console.WriteLine($"tex[{i}] type={m.Textures[i].Type} flags=0x{m.Textures[i].Flags:X} {texture} {w}x{h} alpha={minA}..{maxA} nonzero={nonzero}/{n} opaque={opaque}/{n} avgRgb={sr/n},{sg/n},{sb/n} colors<={colors.Count}");
}
Console.WriteLine("textureLookup="+string.Join(',',m.TextureLookup));
for(int i=0;i<m.RenderFlags.Count;i++) { var f=m.RenderFlags[i]; Console.WriteLine($"rf[{i}] raw=0x{f.Flags:X} blend={f.BlendingMode} unlit={f.Unlit} unfogged={f.Unfogged} two={f.TwoSided} noztest={f.NoZTest} nozwrite={f.NoZWrite}"); }
for(int i=0;i<m.Submeshes.Count;i++) { var s=m.Submeshes[i]; Console.WriteLine($"sub[{i}] id={s.Id} v={s.VertexStart}+{s.VertexCount} i={s.IndexStart}+{s.IndexCount}"); }
for(int i=0;i<m.Batches.Count;i++) { var b=m.Batches[i]; int ti=b.TextureIndex<m.TextureLookup.Count?m.TextureLookup[b.TextureIndex]:-1; string tn=ti>=0&&ti<m.Textures.Count?m.Textures[ti].Filename:"?"; Console.WriteLine($"batch[{i}] flags=0x{b.Flags:X} priority={b.PriorityPlane} shader={b.ShaderId} sub={b.SubmeshIndex} geo={b.GeosetIndex} color={b.ColorIndex} mat={b.MaterialIndex} layer={b.MaterialLayer} texCount={b.TextureCount} texCombo={b.TextureIndex}->tex[{ti}]={tn} uv={b.TextureCoordIndex} xform={b.TextureTransformIndex} weight={b.TextureWeightIndex} staticAlpha={m.GetStaticAlphaForBatch(b):R}"); }
for(int i=0;i<m.Vertices.Count;i++) { var v=m.Vertices[i]; Console.WriteLine($"v[{i}] p=({v.PosX:R},{v.PosY:R},{v.PosZ:R}) n=({v.NormX:R},{v.NormY:R},{v.NormZ:R}) uv=({v.TexU:R},{v.TexV:R}) weights={v.BoneWeight0},{v.BoneWeight1},{v.BoneWeight2},{v.BoneWeight3} bones={v.BoneIndex0},{v.BoneIndex1},{v.BoneIndex2},{v.BoneIndex3}"); }
for(int i=0;i+2<m.Indices.Count;i+=3) Console.WriteLine($"tri[{i/3}]={m.Indices[i]},{m.Indices[i+1]},{m.Indices[i+2]}");
for(int i=0;i<m.Bones.Count;i++) { var b=m.Bones[i]; Console.WriteLine($"bone[{i}] key={b.KeyBoneId} flags=0x{b.Flags:X} parent={b.ParentBone} pivot={V(b.Pivot)} T={b.Translation.Keys.Count}/{b.Translation.Timestamps.Count} R={b.Rotation.Keys.Count}/{b.Rotation.Timestamps.Count} S={b.Scale.Keys.Count}/{b.Scale.Timestamps.Count}"); if(b.Translation.Keys.Count>0)Console.WriteLine("  T="+string.Join(" | ",b.Translation.Keys.Select(V))); if(b.Rotation.Keys.Count>0)Console.WriteLine("  R="+string.Join(" | ",b.Rotation.Keys.Select(V4))); if(b.Scale.Keys.Count>0)Console.WriteLine("  S="+string.Join(" | ",b.Scale.Keys.Select(V))); }
for(int i=0;i<m.Sequences.Count;i++) { var s=m.Sequences[i]; Console.WriteLine($"seq[{i}] anim={s.AnimationId}:{s.VariationId} t={s.StartTimestamp}..{s.EndTimestamp} flags=0x{s.Flags:X}"); }
for(int i=0;i<m.Colors.Count;i++) Console.WriteLine($"color[{i}] rgbKeys={m.Colors[i].Color.Keys.Count} alphaKeys={m.Colors[i].Alpha.Keys.Count} rgb={string.Join(" | ",m.Colors[i].Color.Keys.Select(V))} alpha={string.Join(',',m.Colors[i].Alpha.Keys)}");
for(int i=0;i<m.TransparencyTracks.Count;i++) Console.WriteLine($"alpha[{i}] static={m.TransparencyStaticAlphas.ElementAtOrDefault(i):R} keys={string.Join(',',m.TransparencyTracks[i].Keys)}");
for(int i=0;i<m.ParticleEmitters.Count;i++) { var e=m.ParticleEmitters[i]; Console.WriteLine($"emitter[{i}] shape={e.Shape} flags=0x{e.Flags:X} bone={e.Bone} tex={e.Texture} blend={e.BlendingType} rate={e.EmissionRate:R} life={e.Lifespan:R} area={e.EmissionAreaLength:R}x{e.EmissionAreaWidth:R} speed={e.EmissionSpeed:R} scale={string.Join(',',e.ScaleKeys.Select(x=>x.ToString("R")))} colors={string.Join(',',e.ColorKeys.Select(x=>x.ToString("X8")))}"); }

const string stormwindPath = @"world\wmo\azeroth\buildings\stormwind\stormwind.wmo";
var swBytes = mpq.ReadFile(stormwindPath) ?? throw new Exception("stormwind root missing");
var sw = WmoReader.ParseRoot(swBytes) ?? throw new Exception("stormwind root parse failed");
Console.WriteLine($"stormwind doodads={sw.Doodads.Count} sets={sw.DoodadSets.Count}");
for (int i=0;i<sw.DoodadSets.Count;i++) Console.WriteLine($"set[{i}] {sw.DoodadSets[i].Name} {sw.DoodadSets[i].FirstInstanceIndex}+{sw.DoodadSets[i].DoodadCount}");
Console.WriteLine("stormwind camera-facing doodad census:");
var unresolvedStormwind = new List<string>();
foreach (var group in sw.Doodads.GroupBy(d => d.ModelPath,
             StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key,
             StringComparer.OrdinalIgnoreCase))
{
    var asset = mpq.ReadFile(group.Key)
        ?? mpq.ReadFile(Path.ChangeExtension(group.Key, ".m2"))
        ?? mpq.ReadFile(Path.ChangeExtension(group.Key, ".mdx"));
    if (asset is null) continue;
    M2Model? candidate = M2Reader.Parse(asset);
    if (candidate is null) continue;

    int unresolved = 0;
    foreach (M2Batch batch in candidate.Batches)
    {
        int lookup = batch.TextureIndex < candidate.TextureLookup.Count
            ? candidate.TextureLookup[batch.TextureIndex] : -1;
        string texture = lookup >= 0 && lookup < candidate.Textures.Count
            ? candidate.Textures[lookup].Filename : "";
        if (texture.Length == 0 || AdtTerrainReader.ReadBlpPixels(data, texture) is null)
            unresolved++;
    }
    if (unresolved > 0)
        unresolvedStormwind.Add($"  {group.Key} placements={group.Count()} " +
            $"bones={candidate.Bones.Count} vertices={candidate.Vertices.Count} " +
            $"batches={candidate.Batches.Count} unresolved={unresolved}");
    if (!DoodadBillboardLaw.RequiresPerInstancePose(candidate)) continue;
    Console.WriteLine($"  {group.Key} placements={group.Count()} bones={candidate.Bones.Count} " +
        $"vertices={candidate.Vertices.Count} batches={candidate.Batches.Count} " +
        $"unresolved={unresolved}");
}
Console.WriteLine("stormwind unresolved mesh-texture census:");
foreach (string row in unresolvedStormwind) Console.WriteLine(row);
var matching = sw.Doodads.Select((d,i)=>(d,i)).Where(x=>x.d.ModelPath.Contains("ASHENVALEWISPS",StringComparison.OrdinalIgnoreCase)).ToArray();
foreach(var x in matching) Console.WriteLine($"swDoodad[{x.i}] path={x.d.ModelPath} p=({x.d.PosX:R},{x.d.PosY:R},{x.d.PosZ:R}) q=({x.d.QuatX:R},{x.d.QuatY:R},{x.d.QuatZ:R},{x.d.QuatW:R}) scale={x.d.Scale:R} color={x.d.ColorR},{x.d.ColorG},{x.d.ColorB},{x.d.ColorA} sets={string.Join(',',sw.DoodadSets.Select((s,i)=>(s,i)).Where(z=>x.i>=z.s.FirstInstanceIndex&&x.i<z.s.FirstInstanceIndex+z.s.DoodadCount).Select(z=>$"{z.i}:{z.s.Name}"))}");
var adt = AdtTerrainReader.ReadFromMpq(data,"Azeroth",48,30) ?? throw new Exception("ADT missing");
Vector3 player = new(-9004.04f,872.255f,29.6207f);
foreach(var w in adt.Wmos?.Where(w=>w.ModelPath.EndsWith("stormwind.wmo",StringComparison.OrdinalIgnoreCase)) ?? [])
{
    var wm = BuildWmoPlacement(w);
    Console.WriteLine($"stormwind placement p=({w.PosX:R},{w.PosY:R},{w.PosZ:R}) rot=({w.RotX:R},{w.RotY:R},{w.RotZ:R}) set={w.DoodadSet} worldOrigin={V(new Vector3(wm.M41,wm.M42,wm.M43))}");
    var activeSets = new HashSet<int>{0}; if(w.DoodadSet>0&&w.DoodadSet<sw.DoodadSets.Count)activeSets.Add(w.DoodadSet);
    foreach(var x in matching)
    {
        if(!activeSets.Any(si=>x.i>=sw.DoodadSets[si].FirstInstanceIndex&&x.i<sw.DoodadSets[si].FirstInstanceIndex+sw.DoodadSets[si].DoodadCount))continue;
        var dm=BuildDoodadPlacement(x.d)*wm;
        var origin=new Vector3(dm.M41,dm.M42,dm.M43);
        Console.WriteLine($" active swDoodad[{x.i}] world={V(origin)} dist={Vector3.Distance(origin,player):R}");
        for(int t=0;t<5;t++) { Vector3 c=Vector3.Zero; for(int j=0;j<3;j++){var vv=m.Vertices[m.Indices[t*3+j]];c+=Vector3.Transform(new Vector3(vv.PosX,vv.PosY,vv.PosZ),dm);} c/=3; Console.WriteLine($"   meshTri[{t}] worldCentre={V(c)} dist={Vector3.Distance(c,player):R}"); }
    }
}
Environment.Exit(0);
static string V(Vector3 v)=>$"({v.X:R},{v.Y:R},{v.Z:R})";
static string V4(Vector4 v)=>$"({v.X:R},{v.Y:R},{v.Z:R},{v.W:R})";
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static Matrix4x4 BuildWmoPlacement(AdtTerrainReader.WmoInstance w)
{
    const float deg=MathF.PI/180f, C=32f*533.33333f;
    var basis=new Matrix4x4(1,0,0,0, 0,0,-1,0, 0,1,0,0, 0,0,0,1);
    var placementToWorld=new Matrix4x4(0,-1,0,0, 0,0,1,0, -1,0,0,0, C,C,0,1);
    float heading=(w.RotY-90f)*deg;
    var rotation=Matrix4x4.CreateRotationX(w.RotZ*deg)*Matrix4x4.CreateRotationZ(-w.RotX*deg)*Matrix4x4.CreateRotationY(heading);
    return basis*rotation*Matrix4x4.CreateTranslation(w.PosX,w.PosY,w.PosZ)*placementToWorld;
}
static Matrix4x4 BuildDoodadPlacement(WmoDoodadDef d)
{
    var m2ToWmo=new Matrix4x4(1,0,0,0, 0,0,1,0, 0,-1,0,0, 0,0,0,1);
    return m2ToWmo*Matrix4x4.CreateScale(d.Scale>0.0001f?d.Scale:1f)*Matrix4x4.CreateFromQuaternion(new Quaternion(d.QuatX,d.QuatY,d.QuatZ,d.QuatW))*Matrix4x4.CreateTranslation(d.PosX,d.PosY,d.PosZ);
}
