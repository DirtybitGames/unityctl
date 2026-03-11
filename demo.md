# UnityCtl — AI-Driven Unity Editor Control

*2026-03-11T07:45:55Z by Showboat 0.6.1*
<!-- showboat-id: 83186fb9-e957-4a8f-a2b8-9f99ad10d721 -->

[github.com/DirtybitGames/unityctl](https://github.com/DirtybitGames/unityctl)

unityctl lets AI agents remote-control a live Unity Editor through a lightweight bridge daemon. No batch mode required — the Editor stays open, interactive, and fully observable.

## Checking Status

First, check that the bridge and Unity Editor are connected.

```bash
unityctl status
```

```output
Project Status:
  Path: ~/Workspaces/unityctl/unity-project
  ID: proj-7b58d11d

Unity Editor: [+] Running

Bridge: [+] Running
  PID: 26380
  Port: 64197

Connection: [+] Unity connected to bridge
```

## Observing the Scene

The `snapshot` command gives an LLM-friendly view of the scene hierarchy, including instance IDs that can be used to target specific objects.

```bash
unityctl snapshot
```

```output
Scene: TestScene
5 root objects

Main Camera [i:47012] Camera, AudioListener  tag:MainCamera
  pos(0.0, 0.0, -10.0)
MyCube [i:-5074] MeshFilter, BoxCollider, MeshRenderer, TestComponent  prefab:Assets/Prefabs/TestCube.prefab
  pos(0.0, 1.0, 0.0)
MyGroup [i:-5098]  prefab:Assets/Prefabs/TestGroup.prefab
  pos(5.0, 0.0, 0.0)
  TestCube [i:-5102] MeshFilter, BoxCollider, MeshRenderer, TestComponent  prefab:Assets/Prefabs/TestCube.prefab
    pos(1.0, 0.0, 0.0)
  InnerSphere [i:-5114] MeshFilter, SphereCollider, MeshRenderer
    pos(-1.0, 0.0, 0.0)
MyVariant [i:-5086] MeshFilter, BoxCollider, MeshRenderer, TestComponent  prefab:Assets/Prefabs/TestCubeVariant.prefab
  pos(-5.0, 0.0, 0.0)
Ground [i:47002] MeshFilter, MeshCollider, MeshRenderer
  pos(0.0, -0.5, 0.0) scale(10.0, 1.0, 10.0)
```

Drill into a specific object to see all its component properties:

```bash
unityctl snapshot --id -5074 --components
```

```output
MyCube [i:-5074]  prefab:Assets/Prefabs/TestCube.prefab
  Transform:
    position: (0.0, 1.0, 0.0)
  Prefab: Assets/Prefabs/TestCube.prefab (Regular)
  MeshFilter:
    mesh: "Cube"
  BoxCollider:
    material: null
    include Layers: 0
    exclude Layers: 0
    layer Override Priority: 0
    is Trigger: False
    provides Contacts: False
    size: (1.0, 1.0, 1.0)
    center: (0.0, 0.0, 0.0)
  MeshRenderer:
    cast Shadows: On
    receive Shadows: True
    dynamic Occludee: True
    static Shadow Caster: False
    motion Vectors: Per Object Motion
    light Probe Usage: 1
    reflection Probe Usage: 1
    ray Tracing Mode: 2
    ray Trace Procedural: False
    ray Tracing Accel Struct Build Flags Override: False
    ray Tracing Accel Struct Build Flags: 1
    small Mesh Culling: True
    rendering Layer Mask: 1
    renderer Priority: 0
    materials:
      - "Lit"
    probe Anchor: null
    light Probe Volume Override: null
    lightmap Parameters: null
  TestComponent:
    speed: 20
    health: 100
    display Name: Default
```

Let's capture a screenshot of the current scene view.

```bash
unityctl screenshot capture -o demo-screenshot-1.png
```

```output
Screenshot captured: unity-project\Screenshots\demo-screenshot-1.png
Resolution: 1940x1024
```

```bash {image}
\![Scene view before modifications](unity-project/Screenshots/demo-screenshot-1.png)
```

![Scene view before modifications](92e68eaf-2026-03-11.png)

## Querying with Script Eval

`script eval` executes C# expressions directly inside the Unity Editor process. Common namespaces (UnityEngine, UnityEditor, System) are auto-imported.

```bash
unityctl script eval 'Application.unityVersion'
```

```output
Result: "6000.0.63f1"
```

```bash
unityctl script eval 'GameObject.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length'
```

```output
Result: 1
```

Target a specific object by instance ID with `--id`. The object is available as `target` in the expression:

```bash
unityctl script eval --id -5074 'target.GetComponent<TestComponent>().speed'
```

```output
Result: 20.0
```

## Modifying the Scene

Move MyCube up and change its color using eval expressions:

```bash
unityctl script eval --id -5074 'target.transform.position = new Vector3(0, 3, 0); return target.transform.position'
```

```output
Result: (0.00, 3.00, 0.00)
```

```bash
unityctl script eval --id -5074 "var r = target.GetComponent<Renderer>(); r.sharedMaterial = new Material(r.sharedMaterial); r.sharedMaterial.color = Color.red; return \"painted red\";"
```

```output
Result: "painted red"
```

Screenshot after moving and recoloring the cube:

```bash
unityctl screenshot capture -o demo-screenshot-2.png
```

```output
Screenshot captured: unity-project\Screenshots\demo-screenshot-2.png
Resolution: 1940x1024
```

```bash {image}
\![Scene after moving cube up and painting it red](unity-project/Screenshots/demo-screenshot-2.png)
```

![Scene after moving cube up and painting it red](d1f7b74e-2026-03-11.png)

## Play Mode & Logs

Enter play mode and check the console output. The HelloWorld script logs a message on Start.

```bash
unityctl play enter
```

```output
Play mode: EnteredPlayMode
```

```bash
unityctl script eval "Debug.Log(\"Runtime check: \" + Time.frameCount + \" frames\"); return \"logged\";"
```

```output
Result: "logged"
```

Query runtime state while the game is running:

```bash
unityctl script eval "new { frame = Time.frameCount, dt = Time.deltaTime, fps = 1f / Time.deltaTime }"
```

```output
Result: {
  "frame": 14351,
  "dt": 0.00473450264,
  "fps": 211.215424
}
```

```bash
unityctl screenshot capture -o demo-screenshot-3.png
```

```output
Screenshot captured: unity-project\Screenshots\demo-screenshot-3.png
Resolution: 1940x1024
```

```bash {image}
\![Game view in play mode](unity-project/Screenshots/demo-screenshot-3.png)
```

![Game view in play mode](1db62652-2026-03-11.png)

```bash
unityctl play exit
```

```output
Play mode: ExitingPlayMode
```

## Writing & Compiling Scripts

Write a new MonoBehaviour to the project, then trigger compilation with `asset refresh`. Compilation errors are returned inline.

```bash
cat > unity-project/Assets/Scripts/Spinner.cs << 'SCRIPT'
using UnityEngine;

public class Spinner : MonoBehaviour
{
    public float speed = 90f;

    void Update()
    {
        transform.Rotate(Vector3.up, speed * Time.deltaTime);
    }
}
SCRIPT
echo 'Wrote Spinner.cs'
```

```output
Wrote Spinner.cs
```

```bash
unityctl asset refresh
```

```output
Asset refresh completed (compilation succeeded)
```

Attach the new Spinner component to MyCube at runtime using eval:

```bash
unityctl script eval --id -5074 "target.AddComponent<Spinner>(); return \"Spinner attached to MyCube\";"
```

```output
Result: "Spinner attached to MyCube"
```

Enter play mode and record the spinning cube:

```bash
unityctl play enter
```

```output
Play mode: EnteredPlayMode
```

```bash
unityctl record start --duration 3 --output demo-spin
```

```output
Saved unity-project\Recordings\demo-spin.mp4 (3,0s, 90 frames)
```

```bash
unityctl play exit
```

```output
Play mode: ExitingPlayMode
```

## Full Script Execution

For larger scripts, use `script execute -f` with a file containing a class with a static `Main()` method:

```bash
cat > /tmp/SceneReport.cs << 'SCRIPT'
using UnityEngine;
using System.Linq;

public class Script
{
    public static object Main()
    {
        var objects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        return new
        {
            totalObjects = objects.Length,
            withRenderers = objects.Count(o => o.GetComponent<Renderer>() != null),
            withColliders = objects.Count(o => o.GetComponent<Collider>() != null),
            objectNames = objects.Select(o => o.name).OrderBy(n => n).ToArray()
        };
    }
}
SCRIPT
echo 'Wrote SceneReport.cs'
```

```output
Wrote SceneReport.cs
```

```bash
unityctl script execute -f /tmp/SceneReport.cs
```

```output
Result: {
  "totalObjects": 7,
  "withRenderers": 5,
  "withColliders": 5,
  "objectNames": [
    "Ground",
    "InnerSphere",
    "Main Camera",
    "MyCube",
    "MyGroup",
    "MyVariant",
    "TestCube"
  ]
}
```

## Running Tests

Run the project's edit-mode tests directly from the CLI:

```bash
unityctl test run
```

```output
Running tests...
Tests completed in 1,5s
Passed: 5, Failed: 0, Skipped: 0

```

## Scene Management

```bash
unityctl scene list --source all
```

```output
Found 5 scene(s):
  [ ] Assets/Scenes/SampleScene.unity
  [ ] Assets/Scenes/TestScene.unity
  [ ] Assets/Settings/Scenes/URP2DSceneTemplate.unity
  [ ] Packages/com.unity.render-pipelines.universal/Editor/SceneTemplates/Basic.unity
  [ ] Packages/com.unity.render-pipelines.universal/Editor/SceneTemplates/Standard.unity
```

```bash
unityctl scene load Assets/Scenes/SampleScene.unity
```

```output
Scene loaded: Assets/Scenes/SampleScene.unity
```

```bash
unityctl snapshot
```

```output
Scene: SampleScene
3 root objects

Main Camera [i:47292] Camera, AudioListener, UniversalAdditionalCameraData  tag:MainCamera
  pos(0.0, 0.0, -10.0)
Global Light 2D [i:47302] Light2D
  pos(0.0, 0.0, 0.0)
HelloWorldObject [i:47286] HelloWorld
  pos(0.0, 0.0, 0.0)
```

```bash
unityctl screenshot capture -o demo-screenshot-4.png
```

```output
Screenshot captured: unity-project\Screenshots\demo-screenshot-4.png
Resolution: 1940x1024
```

```bash {image}
\![SampleScene — a minimal 2D scene with HelloWorld](unity-project/Screenshots/demo-screenshot-4.png)
```

![SampleScene — a minimal 2D scene with HelloWorld](b071280b-2026-03-11.png)

## Cleanup

Restore the original scene and remove the demo script:

```bash
unityctl scene load Assets/Scenes/TestScene.unity
```

```output
Scene loaded: Assets/Scenes/TestScene.unity
```

```bash
rm unity-project/Assets/Scripts/Spinner.cs unity-project/Assets/Scripts/Spinner.cs.meta && unityctl asset refresh && echo 'Spinner.cs removed'
```

```output
Asset refresh completed (compilation succeeded)
Spinner.cs removed
```
