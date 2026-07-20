using System;
using System.Numerics;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// The REAL view state of the game camera, read from the matrices the renderer actually
/// uses — the same ViewMatrix and ProjectionMatrix that WorldToScreen multiplies through.
///
/// This exists because reconstructing the view from the orbit angles (DirH/DirV/FoV)
/// requires knowing the game's conventions for them, and guessing those is how the first
/// two versions of the dynamic camera went wrong (a flipped pitch put the death framing
/// underground; the debug overlay's predicted projections landed nowhere near the game's
/// own). Reading the matrices removes every guess: the lens comes straight off the
/// projection matrix (tanHalf = 1/M11, 1/M22 for a standard perspective matrix), and the
/// forward axis off the view matrix, with the row/column and sign ambiguity settled at
/// runtime by checking against the camera→look-at direction.
///
/// The one game convention that still matters — which way DirV runs — is handled by the
/// controller's write-feedback loop, not by anything here.
/// </summary>
public readonly struct GameCameraView
{
    public readonly Vector3 Position;
    public readonly Vector3 Forward;
    /// <summary>SIGNED projection scales for the no-roll basis built by
    /// DynamicCameraSolver.BasisFromForward. Magnitudes come off the projection matrix
    /// diagonal; SIGNS come from comparing our cross-product right/up against the view
    /// matrix's actual axes — our construction has no way to know the game's handedness,
    /// and getting it wrong mirrors the frame (which showed up in testing as every
    /// projected point sitting at the right height on the wrong side).</summary>
    public readonly float TanHalfH;
    public readonly float TanHalfV;
    /// <summary>The game camera's FoV FIELD (what we write), whatever it means optically.
    /// Its relation to the real lens is measured, not assumed: TanHalfV / tan(GameFov/2).</summary>
    public readonly float GameFov;
    public readonly float DirH;
    public readonly float DirV;
    /// <summary>Diagnostics for the overlay: which matrix convention won, and how well.</summary>
    public readonly bool AxesAreRows;
    public readonly float AxisScore;

    private GameCameraView(Vector3 position, Vector3 forward, float tanHalfH, float tanHalfV,
        float gameFov, float dirH, float dirV, bool axesAreRows, float axisScore)
    {
        Position = position;
        Forward = forward;
        TanHalfH = tanHalfH;
        TanHalfV = tanHalfV;
        GameFov = gameFov;
        DirH = dirH;
        DirV = dirV;
        AxesAreRows = axesAreRows;
        AxisScore = axisScore;
    }

    public static unsafe bool TryRead(out GameCameraView view)
    {
        view = default;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return false;

            var gameCam = camMgr->Camera;
            var sceneCam = &gameCam->CameraBase.SceneCamera;
            var rc = sceneCam->RenderCamera;
            if (rc == null)
                return false;

            var origin = new Vector3(rc->Origin.X, rc->Origin.Y, rc->Origin.Z);
            var lookAt = sceneCam->LookAtVector;
            var lookDir = new Vector3(lookAt.X, lookAt.Y, lookAt.Z) - origin;
            if (lookDir.LengthSquared() < 1e-6f)
                return false;
            lookDir = Vector3.Normalize(lookDir);

            // The view matrix's axes are its rows or its columns depending on the library's
            // vector convention, and the z axis may point either way. Decide by which z
            // candidate best lines up with where the camera is actually looking.
            var m = sceneCam->ViewMatrix;
            var rowZ = new Vector3(m.M31, m.M32, m.M33);
            var colZ = new Vector3(m.M13, m.M23, m.M33);
            var rowScore = AlignScore(rowZ, lookDir);
            var colScore = AlignScore(colZ, lookDir);
            var axesAreRows = rowScore >= colScore;
            var score = MathF.Max(rowScore, colScore);

            Vector3 zAxis, xAxis, yAxis;
            if (axesAreRows)
            {
                zAxis = rowZ;
                xAxis = new Vector3(m.M11, m.M12, m.M13);
                yAxis = new Vector3(m.M21, m.M22, m.M23);
            }
            else
            {
                zAxis = colZ;
                xAxis = new Vector3(m.M11, m.M21, m.M31);
                yAxis = new Vector3(m.M12, m.M22, m.M32);
            }

            Vector3 fwd;
            if (score > 0.5f && zAxis.LengthSquared() > 1e-6f)
            {
                fwd = Vector3.Normalize(zAxis);
                if (Vector3.Dot(fwd, lookDir) < 0f)
                    fwd = -fwd; // z axis may be −forward (right-handed view); right/up rows stay as they are
            }
            else
            {
                // Matrix torn mid-update or a convention we did not anticipate: the look
                // direction is always approximately right, and the axis signs below fall
                // back to + (cross-product construction).
                fwd = lookDir;
                xAxis = Vector3.Zero;
                yAxis = Vector3.Zero;
            }

            // Our no-roll basis for this forward — the one every projection in the solver
            // and the overlay actually uses.
            var myRight = Vector3.Cross(Vector3.UnitY, fwd);
            if (myRight.LengthSquared() < 1e-6f)
                myRight = new Vector3(-fwd.Z, 0f, fwd.X);
            myRight = Vector3.Normalize(myRight);
            var myUp = Vector3.Cross(fwd, myRight);

            var signH = xAxis.LengthSquared() > 1e-6f && Vector3.Dot(myRight, xAxis) < 0f ? -1f : 1f;
            var signV = yAxis.LengthSquared() > 1e-6f && Vector3.Dot(myUp, yAxis) < 0f ? -1f : 1f;

            // Real lens, straight off the projection matrix WorldToScreen uses: for a
            // perspective matrix the diagonal is 1/tan(half-fov) per axis. Signs of the
            // diagonal itself carry through — only the axis-handedness sign is ours to fix.
            var proj = rc->ProjectionMatrix;
            if (MathF.Abs(proj.M11) < 1e-6f || MathF.Abs(proj.M22) < 1e-6f)
                return false;

            view = new GameCameraView(
                origin,
                fwd,
                signH / proj.M11,
                signV / proj.M22,
                gameCam->FoV,
                gameCam->DirH,
                gameCam->DirV,
                axesAreRows,
                score);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float AlignScore(Vector3 candidate, Vector3 lookDir)
    {
        var len = candidate.Length();
        if (len < 1e-4f)
            return 0f;
        return MathF.Abs(Vector3.Dot(candidate / len, lookDir));
    }
}
