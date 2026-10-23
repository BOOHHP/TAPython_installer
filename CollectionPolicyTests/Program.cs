using System.Text.Json.Nodes;
using TAPythonInstaller;

var shell = ToolCollectionPolicy.Parse(JsonNode.Parse("""
{
  "collection": { "id": "ue-python-toolkit", "role": "shell", "uiKind": "tkinter-shell" }
}
""")!.AsObject());
Assert(shell is { Role: "shell", Id: "ue-python-toolkit" }, "shell metadata parses");
AssertThrows(() => ToolCollectionPolicy.Parse(JsonNode.Parse("""
{
  "collection": { "id": "ue-python-toolkit", "role": "shell", "uiKind": "" }
}
""")!.AsObject()), "uiKind");
AssertThrows(() => ToolCollectionPolicy.Parse(JsonNode.Parse("""
{
  "collection": { "id": "ue-python-toolkit", "role": "shell", "unexpected": true }
}
""")!.AsObject()), "未知字段");

var module = ToolCollectionPolicy.Parse(JsonNode.Parse("""
{
  "collection": {
    "id": "ue-python-toolkit",
    "role": "module",
    "moduleId": "batch_offset_actors",
    "categoryPath": ["1_Actor场景", "4_位置变换"],
    "requiredShellVersion": "1.0.0",
    "sourceSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }
}
""")!.AsObject());
Assert(module is { Role: "module", ModuleId: "batch_offset_actors", RequiredShellVersion: "1.0.0" }, "module metadata parses");

AssertThrows(() => ToolCollectionPolicy.ValidateInstall(module, []), "requires Collection Shell");
AssertThrows(() => ToolCollectionPolicy.ValidateInstall(module, [new InstalledCollectionEntry("ue-python-toolkit", "shell", "", "2.0.0")]), "requires Shell 1.0.0");
ToolCollectionPolicy.ValidateInstall(module, [new InstalledCollectionEntry("ue-python-toolkit", "shell", "", "1.0.0")]);
ToolCollectionPolicy.ValidateInstall(module, [new InstalledCollectionEntry("ue-python-toolkit", "shell", "", "v1.0.0")]);
Assert(!ToolCollectionPolicy.CanUninstallShell(shell!, [new InstalledCollectionEntry("ue-python-toolkit", "module", "batch_offset_actors", "1.0.0")]), "shell uninstall blocked while modules remain");
Assert(ToolCollectionPolicy.CanUninstallShell(shell!, []), "shell uninstall allowed without modules");
var otherCollectionModule = module! with { Id = "other-toolkit" };
var otherModule = module with { ModuleId = "other-module" };
Assert(ToolCollectionPolicy.HasSameIdentity(module, module), "same module identity matches");
Assert(!ToolCollectionPolicy.HasSameIdentity(module, otherCollectionModule), "different collections stay isolated");
Assert(!ToolCollectionPolicy.HasSameIdentity(module, otherModule), "different modules stay isolated");

var secondModule = module with { ModuleId = "actor_audit", CategoryPath = ["0_Audit"] };
var installPlan = ToolCollectionPolicy.PlanModuleInstall(shell!, "v1.0.0", [
  new CollectionInstallCandidate("other-collection", "1.0.0", otherCollectionModule),
  new CollectionInstallCandidate("wrong-shell-version", "1.0.0", module with { RequiredShellVersion = "2.0.0" }),
  new CollectionInstallCandidate("uept-batch-offset-actors", "1.0.0", module),
  new CollectionInstallCandidate("uept-actor-audit", "1.0.0", secondModule)
]);
Assert(installPlan.Count == 2, "plan selects modules for the same collection and shell version");
Assert(installPlan[0].Collection.ModuleId == "actor_audit", "plan uses stable category and module ordering");
AssertThrows(() => ToolCollectionPolicy.PlanModuleInstall(shell!, "1.0.0", [
  new CollectionInstallCandidate("first", "1.0.0", module),
  new CollectionInstallCandidate("duplicate", "1.0.1", module)
]), "重复模块");
Assert(ToolCollectionPolicy.ResolvePythonRootPrefix("Python/UEPythonToolkit/Modules/batch_offset_actors") == "UEPythonToolkit/Modules/batch_offset_actors", "v2 pythonRoot becomes the extraction prefix");
Assert(ToolCollectionPolicy.ResolvePythonRootPrefix(@"Python\UEPythonToolkit") == "UEPythonToolkit", "Windows separators normalize in pythonRoot");
Assert(ToolCollectionPolicy.IsIgnorableCompatibilityFile(module, "Python/UEPythonToolkit/__init__.py", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "known empty Collection init is ignored after integrity validation");
Assert(!ToolCollectionPolicy.IsIgnorableCompatibilityFile(module, "Python/UEPythonToolkit/runtime.py", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "other files outside pythonRoot remain blocked");
Assert(!ToolCollectionPolicy.IsIgnorableCompatibilityFile(module, "Python/UEPythonToolkit/__init__.py", 1, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "non-empty compatibility file remains blocked");

Console.WriteLine("Collection policy tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception($"Assertion failed: {message}");
}

static void AssertThrows(Action action, string messagePart)
{
    try
    {
        action();
        throw new Exception($"Expected exception containing: {messagePart}");
    }
    catch (InvalidOperationException error) when (error.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase))
    {
    }
}
