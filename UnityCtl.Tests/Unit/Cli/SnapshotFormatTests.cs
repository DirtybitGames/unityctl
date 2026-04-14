using System.Text;
using UnityCtl.Cli;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class SnapshotFormatTests
{
    private static string Format(SnapshotResult result, bool components = false, bool screen = false, bool isDrillDown = false)
        => SnapshotCommand.BuildSnapshotString(result, components, screen, isDrillDown);

    private static SnapshotComponent C(string typeName) => new() { TypeName = typeName };

    private static string FormatQuery(SnapshotQueryResult result)
        => SnapshotCommand.BuildQueryResultString(result);

    // --- Single scene ---

    [Fact]
    public void SingleScene_ShowsSceneHeader()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "MainScene",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 100, Name = "Camera" } }
        });

        Assert.Contains("Scene: MainScene", output);
        Assert.Contains("1 root objects", output);
        Assert.Contains("Camera [i:100]", output);
    }

    [Fact]
    public void PlayMode_ShowsPlayingIndicator()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "MainScene",
            IsPlaying = true,
            RootObjectCount = 0,
            Objects = Array.Empty<SnapshotObject>()
        });

        Assert.Contains("Scene: MainScene (playing)", output);
    }

    // --- Multi-scene ---

    [Fact]
    public void MultiScene_ShowsSceneSeparators()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "MainScene",
            IsPlaying = false,
            RootObjectCount = 3,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 100, Name = "Camera" },
                new SnapshotObject { InstanceId = 200, Name = "Player" },
                new SnapshotObject { InstanceId = 300, Name = "Enemy" }
            },
            Scenes = new[]
            {
                new SnapshotSceneInfo
                {
                    SceneName = "MainScene",
                    ScenePath = "Assets/Scenes/MainScene.unity",
                    IsActive = true,
                    RootObjectCount = 2,
                    Objects = new[]
                    {
                        new SnapshotObject { InstanceId = 100, Name = "Camera" },
                        new SnapshotObject { InstanceId = 200, Name = "Player" }
                    }
                },
                new SnapshotSceneInfo
                {
                    SceneName = "Level2",
                    ScenePath = "Assets/Scenes/Level2.unity",
                    IsActive = false,
                    RootObjectCount = 1,
                    Objects = new[]
                    {
                        new SnapshotObject { InstanceId = 300, Name = "Enemy" }
                    }
                }
            }
        });

        Assert.Contains("2 scenes loaded", output);
        Assert.Contains("3 root objects", output);
        Assert.Contains("--- MainScene (Assets/Scenes/MainScene.unity) [active] ---", output);
        Assert.Contains("--- Level2 (Assets/Scenes/Level2.unity) ---", output);
        Assert.Contains("Camera [i:100]", output);
        Assert.Contains("Enemy [i:300]", output);
    }

    // --- Prefab ---

    [Fact]
    public void PrefabAsset_ShowsPrefabHeader()
    {
        var output = Format(new SnapshotResult
        {
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 500, Name = "Player" } }
        });

        Assert.Contains("Prefab: Assets/Prefabs/Player.prefab", output);
    }

    [Fact]
    public void PrefabStage_ShowsStageAndUnsaved()
    {
        var output = Format(new SnapshotResult
        {
            Stage = "prefab (isolated)",
            PrefabAssetPath = "Assets/Prefabs/Player.prefab",
            HasUnsavedChanges = true,
            OpenedFromInstanceId = 14200,
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 500, Name = "Player" } }
        });

        Assert.Contains("Stage: prefab (isolated)", output);
        Assert.Contains("Prefab: Assets/Prefabs/Player.prefab", output);
        Assert.Contains("Unsaved changes: yes", output);
        Assert.Contains("Opened from: [i:14200]", output);
    }

    // --- UI info ---

    [Fact]
    public void UIButton_ShowsRectTextInteractable()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S",
            IsPlaying = false,
            RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 200, Name = "Button",
                    Text = "Click Me", Interactable = true,
                    Rect = "rect(0, 0, 200, 50)",
                    Anchors = "anchor(0.5-0.5, 0.5-0.5)",
                    Pivot = "(0.5, 0.5)"
                }
            }
        });

        Assert.Contains("rect(0, 0, 200, 50) anchor(0.5-0.5, 0.5-0.5) pivot(0.5, 0.5)", output);
        Assert.Contains("\"Click Me\" interactable", output);
    }

    [Fact]
    public void DisabledButton_ShowsDisabled()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 201, Name = "LockedButton", Text = "Locked", Interactable = false }
            }
        });

        Assert.Contains("\"Locked\" disabled", output);
    }

    [Fact]
    public void InteractableWithoutText_ShowsInteractableAlone()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject { InstanceId = 202, Name = "IconButton", Interactable = true }
            }
        });

        Assert.Contains("interactable", output);
    }

    // --- Screen-space info ---

    [Fact]
    public void Screen_VisibleHittable_ShowsScreenRectAndHittable()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "M", IsPlaying = true, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 300, Name = "PlayButton",
                    ScreenRect = "screen(640, 400, 220, 101)", Visible = true, Hittable = true
                }
            }
        }, screen: true);

        Assert.Contains("screen(640, 400, 220, 101) visible hittable", output);
    }

    [Fact]
    public void Screen_BlockedBy_ShowsBlocker()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "M", IsPlaying = true, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 210, Name = "HealthBar",
                    ScreenRect = "screen(0, 580, 300, 20)", Visible = true, Hittable = false, BlockedBy = 250
                }
            }
        }, screen: true);

        Assert.Contains("screen(0, 580, 300, 20) visible blocked-by:[i:250]", output);
    }

    [Fact]
    public void Screen_OffScreen_ShowsOffScreen()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "M", IsPlaying = true, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 400, Name = "OffScreenBtn",
                    ScreenRect = "screen(1568, 242, 392, 48)", Visible = false
                }
            }
        }, screen: true);

        Assert.Contains("screen(1568, 242, 392, 48) off-screen", output);
    }

    [Fact]
    public void Screen_NotRequested_OmitsScreenInfo()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "M", IsPlaying = true, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 300, Name = "PlayButton",
                    ScreenRect = "screen(640, 400, 220, 101)", Visible = true, Hittable = true
                }
            }
        }, screen: false);

        Assert.DoesNotContain("screen(", output);
        Assert.DoesNotContain("hittable", output);
    }

    // --- Inactive, tags, layers, prefab annotations ---

    [Fact]
    public void Inactive_ShowsBracketedInactive()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 1, Name = "Hidden", Active = false } }
        });

        Assert.Contains("Hidden [inactive] [i:1]", output);
    }

    [Fact]
    public void TagAndLayer_ShowsInline()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[] { new SnapshotObject { InstanceId = 1, Name = "Player", Tag = "Player", Layer = "Characters" } }
        });

        Assert.Contains("tag:Player", output);
        Assert.Contains("layer:Characters", output);
    }

    [Fact]
    public void PrefabInstance_ShowsPrefabPath()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1, Name = "Enemy",
                    IsPrefabInstanceRoot = true, PrefabAssetPath = "Assets/Prefabs/Enemy.prefab"
                }
            }
        });

        Assert.Contains("prefab:Assets/Prefabs/Enemy.prefab", output);
    }

    // --- Children / depth ---

    [Fact]
    public void Children_IndentsAndShowsTruncation()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1, Name = "Parent", ChildCount = 2,
                    Children = new[]
                    {
                        new SnapshotObject { InstanceId = 2, Name = "Child1", ChildCount = 3 },
                        new SnapshotObject { InstanceId = 3, Name = "Child2" }
                    }
                }
            }
        });

        Assert.Contains("  Child1 [i:2]", output);
        Assert.Contains("  Child2 [i:3]", output);
        Assert.Contains("(3 children)", output);
    }

    // --- Components (drill-down) ---

    [Fact]
    public void Components_ShowsPropertyDrillDown()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1, Name = "Player",
                    Position = "(0, 1, 0)", Rotation = "(0, 0, 0, 1)", Scale = "(1, 1, 1)",
                    Components = new[]
                    {
                        new SnapshotComponent
                        {
                            TypeName = "Rigidbody",
                            Properties = new Dictionary<string, object> { { "mass", 5 } }
                        }
                    }
                }
            }
        }, components: true);

        Assert.Contains("Transform:", output);
        Assert.Contains("position: (0, 1, 0)", output);
        Assert.Contains("rotation: (0, 0, 0, 1)", output);
        Assert.Contains("localScale: (1, 1, 1)", output);
        Assert.Contains("Rigidbody:", output);
        Assert.Contains("mass: 5", output);
        // With --components, component names should NOT be on the main line
        Assert.DoesNotContain("Player [i:1] Rigidbody", output);
    }

    [Fact]
    public void Components_WithRect_ShowsRectTransform()
    {
        var output = Format(new SnapshotResult
        {
            SceneName = "S", IsPlaying = false, RootObjectCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1, Name = "Panel",
                    Rect = "rect(0, 0, 200, 50)", Anchors = "anchor(0-1, 0-1)", Pivot = "(0.5, 0.5)",
                    Components = new[] { new SnapshotComponent { TypeName = "Image" } }
                }
            }
        }, components: true);

        Assert.Contains("RectTransform:", output);
        Assert.Contains("rect: rect(0, 0, 200, 50)", output);
        Assert.Contains("anchors: anchor(0-1, 0-1)", output);
        Assert.Contains("pivot: (0.5, 0.5)", output);
    }

    // --- Query formatting ---

    [Fact]
    public void Query_PlayMode_ShowsHits()
    {
        var output = FormatQuery(new SnapshotQueryResult
        {
            X = 400, Y = 300, Mode = "play", ScreenWidth = 1920, ScreenHeight = 1080,
            UiHits = new[]
            {
                new SnapshotQueryHit { InstanceId = 200, Name = "Button", Path = "Canvas/Panel/Button", Text = "Click Me", Interactable = true },
                new SnapshotQueryHit { InstanceId = 100, Name = "Panel", Path = "Canvas/Panel" }
            }
        });

        Assert.Contains("Hit at (400, 300):", output);
        Assert.DoesNotContain("edit mode", output);
        Assert.Contains("UI (2 hits):", output);
        Assert.Contains("1. Button [i:200] — Canvas/Panel/Button \"Click Me\" interactable", output);
        Assert.Contains("2. Panel [i:100] — Canvas/Panel", output);
    }

    [Fact]
    public void Query_EditMode_ShowsApproximateWarning()
    {
        var output = FormatQuery(new SnapshotQueryResult
        {
            X = 100, Y = 100, Mode = "edit-approximate", ScreenWidth = 1920, ScreenHeight = 1080,
            UiHits = new[] { new SnapshotQueryHit { InstanceId = 50, Name = "Bg", Path = "Canvas/Bg" } }
        });

        Assert.Contains("[edit mode — hit ordering is approximate]", output);
    }

    [Fact]
    public void Query_NoHits_ShowsNothing()
    {
        var output = FormatQuery(new SnapshotQueryResult
        {
            X = 0, Y = 0, Mode = "play", ScreenWidth = 1920, ScreenHeight = 1080
        });

        Assert.Contains("(nothing)", output);
    }

    [Fact]
    public void Query_DisabledHit_ShowsDisabled()
    {
        var output = FormatQuery(new SnapshotQueryResult
        {
            X = 100, Y = 100, Mode = "play", ScreenWidth = 1920, ScreenHeight = 1080,
            UiHits = new[]
            {
                new SnapshotQueryHit { InstanceId = 300, Name = "LockedBtn", Path = "Canvas/LockedBtn", Interactable = false }
            }
        });

        Assert.Contains("LockedBtn [i:300] — Canvas/LockedBtn disabled", output);
    }

    // --- Filter: breadcrumb + match subtree ---

    [Fact]
    public void Filter_Match_ShowsBreadcrumbAndSubtree()
    {
        var result = new SnapshotResult
        {
            SceneName = "Clan",
            IsPlaying = true,
            RootObjectCount = 4,
            MatchCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1311, Name = "ActionButton", Active = true,
                    Path = "UISceneCanvas/IgnoreSafeArea/PopupContainer/ActionButton",
                    Interactable = true, Layer = "UI",
                    Text = "Claim",
                    Rect = "rect(-100, -40, 200, 80)",
                    ChildCount = 2,
                    Children = new[]
                    {
                        new SnapshotObject { InstanceId = 1312, Name = "ButtonIcon", Layer = "UI" },
                        new SnapshotObject { InstanceId = 1313, Name = "ButtonLabel", Layer = "UI", Text = "Claim" }
                    }
                }
            }
        };

        var output = Format(result);

        // Breadcrumb path
        Assert.Contains("UISceneCanvas / IgnoreSafeArea / PopupContainer /", output);
        // Match object indented under breadcrumb
        Assert.Contains("  ActionButton [i:1311]", output);
        // Children visible
        Assert.Contains("ButtonIcon", output);
        Assert.Contains("ButtonLabel", output);
        // Header shows match count
        Assert.Contains("4 root objects, 1 matched", output);
        // Noise from sibling branches is gone — only the match path + subtree
        Assert.DoesNotContain("MainContent", output);
        Assert.DoesNotContain("TopBar", output);
        // Compact output
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length < 15, $"Expected compact output but got {lines.Length} lines");
    }

    [Fact]
    public void Filter_Match_DepthRelativeToMatch()
    {
        // With the new behavior, depth is relative to the match.
        // depth=1 shows ActionButton + one level of children.
        // The match is always visible regardless of how deep it was in the original tree.
        var result = new SnapshotResult
        {
            SceneName = "Clan",
            IsPlaying = true,
            RootObjectCount = 4,
            MatchCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1311, Name = "ActionButton", Active = true,
                    Path = "UISceneCanvas/IgnoreSafeArea/PopupContainer/ActionButton",
                    Layer = "UI", Text = "Claim",
                    ChildCount = 2,
                    Children = new[]
                    {
                        new SnapshotObject { InstanceId = 1312, Name = "ButtonIcon", ChildCount = 1 },
                        new SnapshotObject { InstanceId = 1313, Name = "ButtonLabel", Text = "Claim" }
                    }
                }
            }
        };

        var output = Format(result);

        // Match IS visible (unlike old behavior where depth from root could hide it)
        Assert.Contains("ActionButton", output);
        Assert.Contains("ButtonIcon", output);
        // Truncation marker shows there's more to drill into with --id
        Assert.Contains("(1 children)", output);
    }

    [Fact]
    public void Filter_MultipleMatches_ShowsSeparateBlocks()
    {
        var result = new SnapshotResult
        {
            SceneName = "Menu",
            IsPlaying = false,
            RootObjectCount = 3,
            MatchCount = 2,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 100, Name = "PlayButton", Active = true,
                    Path = "Canvas/MainPanel/PlayButton",
                    Interactable = true, Text = "Play"
                },
                new SnapshotObject
                {
                    InstanceId = 200, Name = "QuitButton", Active = true,
                    Path = "Canvas/MainPanel/QuitButton",
                    Interactable = true, Text = "Quit"
                }
            }
        };

        var output = Format(result);

        Assert.Contains("3 root objects, 2 matched", output);
        // Each match has its own breadcrumb
        Assert.Contains("Canvas / MainPanel /", output);
        Assert.Contains("PlayButton [i:100]", output);
        Assert.Contains("QuitButton [i:200]", output);
    }

    [Fact]
    public void Filter_RootMatch_NoBreadcrumb()
    {
        var result = new SnapshotResult
        {
            SceneName = "S",
            IsPlaying = false,
            RootObjectCount = 3,
            MatchCount = 1,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 100, Name = "Camera", Active = true,
                    Path = "Camera" // single segment — root object matched
                }
            }
        };

        var output = Format(result);

        Assert.Contains("Camera [i:100]", output);
        // No breadcrumb line (no " / " separator)
        Assert.DoesNotContain(" / ", output);
        // Not indented — renders at column 0
        Assert.Contains("\nCamera [i:100]", output);
    }

    [Fact]
    public void NoFilter_PathAbsent_UnchangedOutput()
    {
        // Regression guard: objects without Path render identically to pre-change behavior
        var result = new SnapshotResult
        {
            SceneName = "S",
            IsPlaying = false,
            RootObjectCount = 2,
            Objects = new[]
            {
                new SnapshotObject
                {
                    InstanceId = 1, Name = "Camera", Active = true,
                    ChildCount = 1,
                    Children = new[]
                    {
                        new SnapshotObject { InstanceId = 2, Name = "Lens" }
                    }
                },
                new SnapshotObject { InstanceId = 3, Name = "Light" }
            }
        };

        var output = Format(result);

        // Standard format: no breadcrumbs, no match count
        Assert.Contains("Scene: S", output);
        Assert.Contains("2 root objects", output);
        Assert.DoesNotContain("matched", output);
        Assert.Contains("Camera [i:1]", output);
        Assert.Contains("  Lens [i:2]", output);
        Assert.Contains("Light [i:3]", output);
    }

    [Fact]
    public void Filter_MultiScene_ShowsPerSceneMatchCount()
    {
        var result = new SnapshotResult
        {
            IsPlaying = true,
            RootObjectCount = 6,
            MatchCount = 3,
            Objects = Array.Empty<SnapshotObject>(),
            Scenes = new[]
            {
                new SnapshotSceneInfo
                {
                    SceneName = "UI", ScenePath = "Assets/UI.unity",
                    IsActive = true, RootObjectCount = 4, MatchCount = 2,
                    Objects = new[]
                    {
                        new SnapshotObject
                        {
                            InstanceId = 100, Name = "PlayButton", Active = true,
                            Path = "Canvas/PlayButton", Interactable = true
                        },
                        new SnapshotObject
                        {
                            InstanceId = 200, Name = "QuitButton", Active = true,
                            Path = "Canvas/QuitButton", Interactable = true
                        }
                    }
                },
                new SnapshotSceneInfo
                {
                    SceneName = "HUD", ScenePath = "Assets/HUD.unity",
                    IsActive = false, RootObjectCount = 2, MatchCount = 1,
                    Objects = new[]
                    {
                        new SnapshotObject
                        {
                            InstanceId = 300, Name = "SettingsButton", Active = true,
                            Path = "Overlay/SettingsButton", Interactable = true
                        }
                    }
                }
            }
        };

        var output = Format(result);

        // Global header
        Assert.Contains("6 root objects, 3 matched", output);
        // Per-scene counts
        Assert.Contains("4 root objects, 2 matched", output);
        Assert.Contains("2 root objects, 1 matched", output);
        // Breadcrumbs
        Assert.Contains("Canvas /", output);
        Assert.Contains("Overlay /", output);
        // Match objects
        Assert.Contains("PlayButton [i:100]", output);
        Assert.Contains("QuitButton [i:200]", output);
        Assert.Contains("SettingsButton [i:300]", output);
    }
}
