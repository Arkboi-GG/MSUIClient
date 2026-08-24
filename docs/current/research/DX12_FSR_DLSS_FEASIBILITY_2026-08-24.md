# MSUIClient FSR/DLSS and DirectX 12 feasibility report

Date: August 24, 2026

## Executive recommendation

The easiest credible target is **FSR 3.1 upscaling-only on DirectX 12**.

Do not begin with FSR frame generation or DLSS. Neither FSR 2/3 nor DLSS can be
inserted into MSUIClient's current OpenGL 3.3 renderer directly. The dominant
task is converting the renderer to DX12 and generating reliable temporal
inputs, not calling the vendor SDK.

Recommended sequence:

1. Preserve OpenGL while adding a renderer-neutral command/resource layer.
2. Build a DX12 backend and reach native-resolution visual parity.
3. Add explicit scene color, depth, motion-vector, reactive-mask, and UI targets.
4. Integrate FSR 3.1 upscaling through a small native bridge.
5. Add DLSS SR later as another consumer of the same temporal resources.
6. Evaluate frame generation only after upscaling and UI separation are stable.

A production-quality conversion is approximately **21-37 engineer-weeks**, or
**6-9 months for one experienced graphics engineer**. A clear-screen, ImGui,
and terrain DX12 prototype is feasible in roughly **3-6 weeks**.

## Why FSR 3.1 wins

| Option | Situation after DX12 exists | Verdict |
|---|---|---|
| FSR 2 | Open and cross-vendor, but an older integration path with the same temporal requirements | Supported fallback, not the first target |
| FSR 3.1 upscaling | Current AMD API, cross-vendor, signed DLL distribution, no frame-generation complexity | **Recommended first target** |
| FSR 3 frame generation | Adds proxy swapchain, frame pacing, UI composition and more lifetime rules | Later phase |
| DLSS Super Resolution | Reuses the same depth/motion/jitter work, but adds NVIDIA application ID, signed plugins and Streamline integration | Optional second upscaler |
| DLSS Frame Generation | Adds present interception, HUD-less buffers, Reflex/latency work and hardware restrictions | Highest complexity |

AMD explicitly recommends implementing upscaling before frame generation. FSR
3.1 supports DX12 and Vulkan and requires color, depth, motion vectors, jitter,
and preferably reactive/transparency masks. See the
[AMD FSR 3.1 integration guide](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-interpolation/).

FSR 2 ships official DX12 and Vulkan backends; an OpenGL backend would have to
be developed and maintained by MSUI. That would also require raising the
client's OpenGL floor substantially above 3.3 for compute workloads, making it
less attractive than DX12. See the
[AMD FSR 2 integration guide](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/super-resolution-temporal/).

DLSS through Streamline supports DirectX/Vulkan resources but still requires
render-resolution color, depth, motion vectors, jitter, exposure handling, and
display-resolution output. It therefore saves none of the difficult MSUI
renderer work. See the
[NVIDIA DLSS programming guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideDLSS.md).

## Current MSUI renderer assessment

MSUI presently:

- Requests an OpenGL 3.3 core context in `MSUIClient/Engine/ClientWindow.cs`.
- Passes `GL` directly into the game loader in `MSUIClient/Program.cs`.
- Uses an OpenGL-specific ImGui controller in `ClientWindow.cs`.
- Renders into the window's default framebuffer, then copies it for glow and
  painterly processing in `Engine/FfxGlow.cs` and `Engine/PainterlyPass.cs`.
- Uses only current camera matrices; there is no temporal jitter or
  previous-frame transform in `Engine/Camera.cs`.
- Uploads assets through a second shared OpenGL context in
  `Engine/GpuUploadWorker.cs`.

The audit found approximately:

- 54 C# files directly importing OpenGL
- 1,800 direct GL calls
- 27 external shader files plus substantial embedded GLSL
- 37 shader-program construction sites
- 541 name-based uniform assignments
- 447 ImGui image calls currently carrying raw GL texture identifiers

This means DX12 is a complete GPU-backend migration. Gameplay, networking,
collision, MPQ parsing, simulation and most UI layout code can remain.

## DX12 work required

### Device and presentation

Keep Silk.NET Windowing and Input, but create the window without a graphics
context and obtain its native HWND. MSUI would then own:

- DXGI factory and adapter selection
- `ID3D12Device`
- Direct and copy command queues
- Flip-discard swapchain
- Two or three frame contexts
- Command allocators and command lists
- RTV, DSV, sampler and shader-visible descriptor heaps
- GPU fences and frame-latency pacing
- Explicit resource transitions
- Resize, fullscreen, device-removed and fallback handling

Silk.NET provides maintained `Silk.NET.Direct3D12` and `Silk.NET.DXGI`
packages. All Silk dependencies should be upgraded together from the project's
current 2.21 set to 2.23. See the
[Silk.NET package documentation](https://dotnet.github.io/Silk.NET/docs/) and
[Direct3D12 package](https://www.nuget.org/packages/Silk.NET.Direct3D12/).

DX12 presentation requires explicit frames-in-flight control; Microsoft warns
that allowing the CPU to queue frames without fence limiting increases input
latency. See
[Microsoft's DX12 swapchain guidance](https://learn.microsoft.com/en-us/windows/win32/direct3d12/swap-chains).

### Renderer abstraction

A thin `IGraphicsDevice` wrapper is insufficient because the current code
depends on inherited OpenGL state. The abstraction needs:

- Backend-neutral buffer and texture handles
- Command contexts
- Render-pass descriptions
- Pipeline-state keys and a PSO cache
- Vertex/index binding
- Typed constant-buffer blocks
- Descriptor allocation
- Upload and deferred-destruction queues
- Copy, resolve and readback operations
- Resource barriers
- Timestamp queries

Calls such as `Enable`, `Disable`, `BlendFunc`, `DepthMask` and state
save/restore must become explicit immutable PSO descriptions. DX12 cannot
reproduce arbitrary global-state snapshots used by several current passes.

### Shaders

All GLSL must gain HLSL equivalents compiled to DXIL with DXC.

The current string-based `Shader.Set("name", value)` system should become typed
constant blocks allocated from a 256-byte-aligned per-frame upload ring.
Separate blocks should cover:

- Frame and camera constants
- Pass constants
- Material constants
- Object transforms
- Bone palettes
- Particle and procedural-animation values

MSUI's matrix convention must be made explicit in HLSL, preferably with
`row_major` declarations and one standardized multiplication order.

### Textures and streaming uploads

Raw GL texture names must become stable backend-neutral handles containing:

- DX12 resource
- SRV allocation
- Format and dimensions
- Current resource state
- Last-use fence
- Deferred-release information

The hidden OpenGL upload context should be replaced with persistently mapped
upload heaps, batched copy command lists, a copy queue, and fence-controlled
reuse.

### ImGui

ImGui.NET layout and input code can remain, but the OpenGL renderer backend must
be replaced. The DX12 backend needs:

- Per-frame vertex/index upload buffers
- UI root signature and PSO
- Font-atlas SRV
- Scissor rectangles
- Texture descriptor lookup
- Fence-safe descriptor lifetime

Existing `ImTextureID` values should remain opaque `nint` handles backed by a
texture registry. This avoids rewriting hundreds of UI image calls.

## Target temporal render pipeline

```text
Input/update
    |
World at render resolution
  color + depth + motion + reactive/composition + painterly weight
    |
FSR 3.1 upscaling
    |
Glow
    |
Painterly processing
    |
Loading curtain + native-resolution ImGui/UI
    |
Composite and present
```

The existing order in `Program.cs` already has one valuable property: world
rendering and post-processing finish before the HUD. That logical separation
should become separate GPU resources.

Required targets:

- Scene color: initially preserve the current LDR appearance
- Depth: sampleable `D32_FLOAT`
- Motion: `R16G16_FLOAT`
- Reactive mask: `R8_UNORM`
- Transparency/composition mask: `R8_UNORM`
- Painterly style weight: dedicated `R8_UNORM`
- Upscaled HUD-less color: display resolution
- UI color/alpha: display resolution

Temporal upscaling should disable MSAA. Native fallback mode may retain it.

The painterly pass currently stores private material importance in scene alpha
and optionally performs its own `CanvasHeight` spatial scaling. Those concepts
must be separated:

- Move material importance into its own texture.
- Let FSR quality/render scale own world resolution.
- Run painterly styling after FSR at display resolution.
- Preserve the current glow-then-painterly artistic order.

The first integration should remain LDR with exposure 1.0 or automatic
exposure. Converting the whole renderer to linear HDR simultaneously would add
substantial parity risk.

## Motion-vector requirements

The difficult part is maintaining prior-frame state:

- Static terrain/WMO: previous camera matrices and camera-relative origins
- Doodad instances: previous model matrices
- Characters, creatures and mounts: previous model transform and bone palette
- Attached weapons and skinned spell meshes: previous parent and bone transforms
- Water: previous procedural time and displacement
- Foliage: previous wind time; newly scattered blades need history resets
- Particles/weather: previous centers and previous billboard axes
- Ribbons, beams and fishing lines: previous control points
- Portals: initially mark portal contents strongly reactive
- Spawned, hidden, streamed or teleported objects: initialize previous=current
  or reset temporal history

The projection should provide both jittered matrices for rendering and
non-jittered current/previous matrices for motion calculations. UI projection
and culling should remain unjittered.

Reactive coverage should be strongest for particles, additive spells,
precipitation, water and portals, then proportional to alpha for other
transparent materials. AMD's automatic reactive-mask generation is useful for
bootstrapping, but authored material masks should be the final target.

## Native SDK bridge

Keep the DX12 renderer managed through Silk.NET, but isolate AMD/NVIDIA SDKs
behind one small x64 C++ DLL with a flat C ABI.

Suggested exports:

```text
msui_upscale_query_support
msui_upscale_create
msui_upscale_resize
msui_upscale_dispatch
msui_upscale_destroy
```

C# would use source-generated `LibraryImport`, blittable structures and
`SafeHandle`, passing Silk's device, command-list, queue and resource pointers.

For FSR, ship the MSUI bridge plus AMD's signed loader/upscaler DLLs. The bridge
insulates C# from SDK descriptor chains, callbacks, ABI layout and version
churn. See the
[FidelityFX API documentation](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/Kits/FidelityFX/docs/getting-started/ffx-api.md).

DLSS can later use the same bridge boundary. Streamline would need
initialization before DXGI/D3D12 creation and manual interface upgrading because
Silk loads DX12 dynamically rather than linking through Streamline's
interposer. See the
[NVIDIA Streamline manual-hooking guide](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideManualHooking.md).

## Implementation schedule

| Phase | Deliverable | Estimate |
|---|---|---:|
| 0 | Golden captures, performance baselines and renderer contracts | 1-2 weeks |
| 1 | DX12 window/device/swapchain, fences, descriptors, uploads and ImGui | 4-7 weeks |
| 2 | Terrain vertical slice and shader/resource conventions | 3-5 weeks |
| 3 | Main world, characters, particles and HLSL parity | 7-12 weeks |
| 4 | Portals, portraits, minimap, glow, painterly and readbacks | 3-5 weeks |
| 5 | Visual parity, streaming stability and hardware testing | 4-8 weeks |
| 6 | Temporal targets, history and masks | 5-9 weeks |
| 7 | FSR 3.1 dispatch, settings and fallback | 1-2 weeks |
| 8 | Optional frame generation and latency work | 3-5 weeks |

Some work overlaps, but the credible feature-complete range remains
approximately **6-9 months for one engineer**.

## Acceptance gates

Before enabling FSR by default:

- DX12 native mode matches representative OpenGL captures for terrain, WMO,
  characters, water, particles, portals, loading, X-Ray and painterly modes.
- DX12 debug layer and GPU validation report no errors.
- Static geometry produces zero object motion with a static camera.
- Camera pans, rigid motion and skinned limbs show correct debug vectors.
- Particle, water, transparency and portal masks are visible and correctly
  bounded.
- Teleports, map changes, camera jumps and render-scale changes reset history.
- HUD text and images remain pixel-sharp and outside temporal processing.
- Resize, minimize/restore, monitor changes and fullscreen transitions do not
  leak descriptors or retain stale history.
- AMD, NVIDIA and Intel systems fall back cleanly when the selected SDK is
  unavailable.
- FSR Quality mode produces a measurable GPU-time improvement after including
  dispatch cost.
- Missing or invalid vendor DLLs never prevent native rendering.

## Final decision

Proceed only if a DX12 renderer is desirable beyond FSR itself. The DX12
migration represents most of the cost; FSR integration becomes relatively
small once the renderer supplies correct temporal resources.

If the immediate objective is simply better 4K performance without a renderer
migration, a spatial OpenGL upscaler such as FSR 1 or NIS would be dramatically
cheaper, but it would not provide FSR 2/3 or DLSS temporal quality.

For the full path, the recommended target is:

**Dual-backend renderer -> DX12 parity -> FSR 3.1 upscaling-only -> optional
DLSS SR -> optional frame generation.**
