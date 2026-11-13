using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using MoonDedicated;

void Assert(bool valid) { if (!valid) throw new Exception("Loader regression failed"); }
void Fixture(string path, bool referenceOrphan)
{
    var m = new MetadataBuilder();
    m.AddModule(0, m.GetOrAddString("fixture.dll"), m.GetOrAddGuid(Guid.NewGuid()), default, default);
    m.AddAssembly(m.GetOrAddString("fixture"), new Version(1,0), default, default, 0, AssemblyHashAlgorithm.None);
    var fields = MetadataTokens.FieldDefinitionHandle(1);
    var methods = MetadataTokens.MethodDefinitionHandle(1);
    m.AddTypeDefinition(0, default, m.GetOrAddString("<Module>"), default, fields, methods);
    var outer = m.AddTypeDefinition(TypeAttributes.Public, default, m.GetOrAddString("Outer"), default, fields, methods);
    var orphan = m.AddTypeDefinition(TypeAttributes.NestedPrivate, default, m.GetOrAddString("Duplicate"), default, MetadataTokens.FieldDefinitionHandle(referenceOrphan ? 2 : 1), methods);
    m.AddTypeDefinition(TypeAttributes.NestedPrivate, default, m.GetOrAddString("Duplicate"), default, MetadataTokens.FieldDefinitionHandle(referenceOrphan ? 2 : 1), methods);
    var nested = m.AddTypeDefinition(TypeAttributes.NestedPublic, default, m.GetOrAddString("KeptChild"), default, MetadataTokens.FieldDefinitionHandle(referenceOrphan ? 2 : 1), methods);
    m.AddNestedType(nested, outer);
    if (referenceOrphan) {
        var signature = new BlobBuilder();
        new BlobEncoder(signature).FieldSignature().Type(orphan, false);
        m.AddFieldDefinition(FieldAttributes.Public, m.GetOrAddString("unsafeReference"), m.GetOrAddBlob(signature));
    }
    var output = new BlobBuilder();
    new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(m), new BlobBuilder()).Serialize(output);
    File.WriteAllBytes(path, output.ToArray());
}
string root = Path.Combine(Path.GetTempPath(), "moon-loader-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    string path = Path.Combine(root, "fixture.dll"); Fixture(path, false);
    var before = SHA256.HashData(File.ReadAllBytes(path));
    Assert(LoaderCompatibility.OrphanCount(path) == 2);
    Assert(LoaderCompatibility.RepairGeneratedAssembly(path) == 2);
    Assert(LoaderCompatibility.OrphanCount(path) == 0);
    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(path)) {
        Assert(module.Types.Any(t => t.Name == "Outer" && t.NestedTypes.Single().Name == "KeptChild"));
        Assert(module.Types.All(t => t.Name != "Duplicate"));
    }
    var backup = Directory.GetFiles(root, "*.before-moon-repair-*").Single();
    Assert(SHA256.HashData(File.ReadAllBytes(backup)).SequenceEqual(before));
    var repaired = File.ReadAllBytes(path);
    Assert(LoaderCompatibility.RepairGeneratedAssembly(path) == 0);
    Assert(File.ReadAllBytes(path).SequenceEqual(repaired));
    Console.WriteLine("Malformed nested metadata repaired; valid nesting and backup preserved; repeat is unchanged: passed");
    string unsafePath = Path.Combine(root, "referenced.dll"); Fixture(unsafePath, true);
    var unsafeBefore = File.ReadAllBytes(unsafePath);
    bool rejected = false;
    try { LoaderCompatibility.RepairGeneratedAssembly(unsafePath); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected && File.ReadAllBytes(unsafePath).SequenceEqual(unsafeBefore));
    Assert(!Directory.GetFiles(root, "referenced.dll.moon-repair-*").Any());
    Console.WriteLine("Referenced orphan is refused without changing original: passed");
} finally { Directory.Delete(root, true); }
