"""
Ragdoll log analyzer and visualizer.
Parses ragdoll logs, checks for physics issues, and generates diagnostic visualizations.

Usage: python tools/analyze_ragdoll.py temp/ragdoll_log.txt
"""
import re
import sys
import math
import numpy as np
from pathlib import Path
from collections import defaultdict

# --- Bone hierarchy definition (mirrors BoneDefs in RagdollController.cs) ---
BONE_HIERARCHY = {
    'j_kosi': None,          # pelvis (root)
    'j_sebo_a': 'j_kosi',    # lower spine
    'j_sebo_b': 'j_sebo_a',  # mid spine
    'j_sebo_c': 'j_sebo_b',  # chest
    'j_kubi': 'j_sebo_c',    # neck
    'j_kao': 'j_kubi',       # head
    'j_ude_a_l': 'j_sebo_c', # left upper arm
    'j_ude_a_r': 'j_sebo_c', # right upper arm
    'j_ude_b_l': 'j_ude_a_l',# left forearm
    'j_ude_b_r': 'j_ude_a_r',# right forearm
    'j_te_l': 'j_ude_b_l',   # left hand
    'j_te_r': 'j_ude_b_r',   # right hand
    'j_asi_a_l': 'j_kosi',   # left thigh
    'j_asi_a_r': 'j_kosi',   # right thigh
    'j_asi_b_l': 'j_asi_a_l',# left shin
    'j_asi_b_r': 'j_asi_a_r',# right shin
    'j_asi_c_l': 'j_asi_b_l',# left foot
    'j_asi_c_r': 'j_asi_b_r',# right foot
}

BONE_CAPSULE_HALF_LENGTHS = {
    'j_kosi': 0.06, 'j_sebo_a': 0.05, 'j_sebo_b': 0.05, 'j_sebo_c': 0.05,
    'j_kubi': 0.03, 'j_kao': 0.04,
    'j_ude_a_l': 0.08, 'j_ude_a_r': 0.08,
    'j_ude_b_l': 0.07, 'j_ude_b_r': 0.07,
    'j_te_l': 0.03, 'j_te_r': 0.03,
    'j_asi_a_l': 0.12, 'j_asi_a_r': 0.12,
    'j_asi_b_l': 0.11, 'j_asi_b_r': 0.11,
    'j_asi_c_l': 0.04, 'j_asi_c_r': 0.04,
}

BONE_CAPSULE_RADII = {
    'j_kosi': 0.12, 'j_sebo_a': 0.10, 'j_sebo_b': 0.10, 'j_sebo_c': 0.10,
    'j_kubi': 0.04, 'j_kao': 0.08,
    'j_ude_a_l': 0.03, 'j_ude_a_r': 0.03,
    'j_ude_b_l': 0.025, 'j_ude_b_r': 0.025,
    'j_te_l': 0.02, 'j_te_r': 0.02,
    'j_asi_a_l': 0.04, 'j_asi_a_r': 0.04,
    'j_asi_b_l': 0.035, 'j_asi_b_r': 0.035,
    'j_asi_c_l': 0.03, 'j_asi_c_r': 0.03,
}

# Expected segment lengths between parent and child bones (approximate, meters)
# These are rough anatomical references for a ~1.7m character
EXPECTED_SEGMENT_LENGTHS = {
    ('j_kosi', 'j_sebo_a'): (0.0, 0.15),    # pelvis to lower spine (often co-located)
    ('j_sebo_a', 'j_sebo_b'): (0.08, 0.15),
    ('j_sebo_b', 'j_sebo_c'): (0.08, 0.15),
    ('j_sebo_c', 'j_kubi'): (0.08, 0.18),
    ('j_kubi', 'j_kao'): (0.04, 0.12),
    ('j_sebo_c', 'j_ude_a_l'): (0.08, 0.20),  # chest to shoulder
    ('j_sebo_c', 'j_ude_a_r'): (0.08, 0.20),
    ('j_ude_a_l', 'j_ude_b_l'): (0.15, 0.30),  # upper arm
    ('j_ude_a_r', 'j_ude_b_r'): (0.15, 0.30),
    ('j_ude_b_l', 'j_te_l'): (0.10, 0.25),     # forearm
    ('j_ude_b_r', 'j_te_r'): (0.10, 0.25),
    ('j_kosi', 'j_asi_a_l'): (0.05, 0.18),      # pelvis to hip
    ('j_kosi', 'j_asi_a_r'): (0.05, 0.18),
    ('j_asi_a_l', 'j_asi_b_l'): (0.20, 0.45),  # thigh
    ('j_asi_a_r', 'j_asi_b_r'): (0.20, 0.45),
    ('j_asi_b_l', 'j_asi_c_l'): (0.15, 0.40),  # shin
    ('j_asi_b_r', 'j_asi_c_r'): (0.15, 0.40),
}

BONE_LABELS = {
    'j_kosi': 'Pelvis', 'j_sebo_a': 'L.Spine', 'j_sebo_b': 'M.Spine',
    'j_sebo_c': 'Chest', 'j_kubi': 'Neck', 'j_kao': 'Head',
    'j_ude_a_l': 'L.Shoulder', 'j_ude_a_r': 'R.Shoulder',
    'j_ude_b_l': 'L.Elbow', 'j_ude_b_r': 'R.Elbow',
    'j_te_l': 'L.Hand', 'j_te_r': 'R.Hand',
    'j_asi_a_l': 'L.Hip', 'j_asi_a_r': 'R.Hip',
    'j_asi_b_l': 'L.Knee', 'j_asi_b_r': 'R.Knee',
    'j_asi_c_l': 'L.Foot', 'j_asi_c_r': 'R.Foot',
}

CONNECTIONS = [(child, parent) for child, parent in BONE_HIERARCHY.items() if parent]


def parse_vec3(s):
    """Parse '(x,y,z)' to numpy array."""
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:3]])


def parse_quat(s):
    """Parse '(x,y,z,w)' to numpy array [x,y,z,w]."""
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:4]])


def quat_to_rotation_matrix(q):
    """Convert quaternion [x,y,z,w] to 3x3 rotation matrix."""
    x, y, z, w = q
    return np.array([
        [1-2*(y*y+z*z), 2*(x*y-z*w),   2*(x*z+y*w)],
        [2*(x*y+z*w),   1-2*(x*x+z*z), 2*(y*z-x*w)],
        [2*(x*z-y*w),   2*(y*z+x*w),   1-2*(x*x+y*y)]
    ])


def angle_between_quats(q1, q2):
    """Angle in degrees between two quaternions."""
    dot = abs(np.dot(q1, q2))
    dot = min(dot, 1.0)
    return math.degrees(2 * math.acos(dot))


def parse_log(filepath):
    """Parse the ragdoll log file into structured data."""
    init_data = {}
    frame_data = defaultdict(dict)
    frame_summaries = {}
    ground_y = None
    skel_pos = None
    skel_rot = None

    with open(filepath, 'r') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue

            # Skeleton transform
            m = re.search(r'Skeleton transform pos=\(([^)]+)\) rot=\(([^)]+)\)', line)
            if m:
                skel_pos = parse_vec3(m.group(1))
                skel_rot = parse_quat(m.group(2))
                continue

            # Ground Y
            m = re.search(r'Raycast ground Y=([-\d.]+)', line)
            if m:
                ground_y = float(m.group(1))
                continue

            # Init bone data
            m = re.search(
                r"\[Ragdoll Init\] '(\w+)' idx=(\d+) "
                r"bonePos=\(([^)]+)\) capsuleCenter=\(([^)]+)\) "
                r"segHalf=([\d.]+) capsuleLen=([\d.]+) "
                r"capsuleY=\(([^)]+)\)",
                line
            )
            if m:
                name = m.group(1)
                init_data[name] = {
                    'idx': int(m.group(2)),
                    'bonePos': parse_vec3(m.group(3)),
                    'capsuleCenter': parse_vec3(m.group(4)),
                    'segHalf': float(m.group(5)),
                    'capsuleLen': float(m.group(6)),
                    'capsuleY': parse_vec3(m.group(7)),
                }
                continue

            # Frame summary
            m = re.search(
                r'\[Ragdoll F(\d+)\] t=([\d.]+)s awake=(\d+)/(\d+) '
                r'yCorr=([-\d.]+) lowestY=([-\d.]+) realGnd=([-\d.]+) '
                r'maxLinVel=([\d.]+) maxAngVel=([\d.]+)',
                line
            )
            if m:
                frame = int(m.group(1))
                frame_summaries[frame] = {
                    't': float(m.group(2)),
                    'awake': int(m.group(3)),
                    'total': int(m.group(4)),
                    'yCorr': float(m.group(5)),
                    'lowestY': float(m.group(6)),
                    'realGnd': float(m.group(7)),
                    'maxLinVel': float(m.group(8)),
                    'maxAngVel': float(m.group(9)),
                }
                continue

            # Per-bone frame data
            m = re.search(
                r"\[Ragdoll F(\d+)\] '(\w+)' "
                r"wPos=\(([^)]+)\) wRot=\(([^)]+)\) "
                r"linV=\(([^)]+)\) angV=\(([^)]+)\) "
                r"awake=(\w+)",
                line
            )
            if m:
                frame = int(m.group(1))
                name = m.group(2)
                frame_data[frame][name] = {
                    'wPos': parse_vec3(m.group(3)),
                    'wRot': parse_quat(m.group(4)),
                    'linV': parse_vec3(m.group(5)),
                    'angV': parse_vec3(m.group(6)),
                    'awake': m.group(7) == 'True',
                }
                continue

    return {
        'skel_pos': skel_pos,
        'skel_rot': skel_rot,
        'ground_y': ground_y,
        'init': init_data,
        'frames': dict(frame_data),
        'summaries': frame_summaries,
    }


def analyze_segment_lengths(data):
    """Check bone-to-bone distances against expected anatomical ranges."""
    print("\n" + "="*80)
    print("SEGMENT LENGTH ANALYSIS")
    print("="*80)

    # Analyze at init and at settled frame
    frames_to_check = ['init']
    sorted_frames = sorted(data['frames'].keys())
    if sorted_frames:
        frames_to_check.append(sorted_frames[0])   # first frame
        # Find the settled frame (last frame before all asleep, or last with data)
        for f in sorted_frames:
            if f in data['summaries'] and data['summaries'][f]['maxLinVel'] < 0.1:
                frames_to_check.append(f)
                break
        if len(frames_to_check) == 2:
            frames_to_check.append(sorted_frames[-1])

    issues = []

    for frame_label in frames_to_check:
        if frame_label == 'init':
            positions = {name: d['bonePos'] for name, d in data['init'].items()}
            label = "INIT"
        else:
            if frame_label not in data['frames']:
                continue
            positions = {name: d['wPos'] for name, d in data['frames'][frame_label].items()}
            label = f"F{frame_label}"

        print(f"\n--- {label} ---")
        print(f"{'Parent':>12} -> {'Child':>12}  {'Dist':>6}  {'Expected':>12}  Status")

        for child, parent in BONE_HIERARCHY.items():
            if parent is None:
                continue
            if child not in positions or parent not in positions:
                continue

            dist = np.linalg.norm(positions[child] - positions[parent])
            key = (parent, child)

            if key in EXPECTED_SEGMENT_LENGTHS:
                lo, hi = EXPECTED_SEGMENT_LENGTHS[key]
                status = "OK" if lo <= dist <= hi else "BAD"
                if dist < lo * 0.5:
                    status = "COLLAPSED"
                elif dist > hi * 1.5:
                    status = "STRETCHED"
                range_str = f"[{lo:.2f}-{hi:.2f}]"
            else:
                status = "?"
                range_str = "N/A"

            flag = " <<<" if status in ("BAD", "COLLAPSED", "STRETCHED") else ""
            print(f"{BONE_LABELS.get(parent, parent):>12} -> {BONE_LABELS.get(child, child):>12}  {dist:.3f}  {range_str:>12}  {status}{flag}")

            if status in ("BAD", "COLLAPSED", "STRETCHED"):
                issues.append((label, parent, child, dist, status))

    return issues


def analyze_ground_penetration(data):
    """Check which bones are below ground level."""
    print("\n" + "="*80)
    print("GROUND PENETRATION ANALYSIS")
    print("="*80)

    ground_y = data['ground_y']
    if ground_y is None:
        print("No ground Y data found.")
        return []

    issues = []
    sorted_frames = sorted(data['frames'].keys())

    for frame in sorted_frames:
        bones = data['frames'][frame]
        below = []
        for name, bd in bones.items():
            y = bd['wPos'][1]
            radius = BONE_CAPSULE_RADII.get(name, 0.03)
            # Bone position is at joint, capsule extends further
            effective_y = y - radius
            penetration = ground_y - effective_y
            if penetration > 0.01:
                below.append((name, y, penetration))

        if below:
            summary = data['summaries'].get(frame, {})
            t = summary.get('t', frame/60.0)
            print(f"\nF{frame} (t={t:.2f}s): {len(below)} bones below ground (Y={ground_y:.3f})")
            for name, y, pen in sorted(below, key=lambda x: -x[2]):
                print(f"  {BONE_LABELS.get(name, name):>12}: Y={y:.3f}, penetration={pen:.3f}m")
                issues.append((frame, name, pen))

    if not issues:
        print("No significant ground penetration detected.")
    return issues


def analyze_velocity_profile(data):
    """Analyze how the ragdoll settles over time."""
    print("\n" + "="*80)
    print("VELOCITY & SETTLING ANALYSIS")
    print("="*80)

    sorted_frames = sorted(data['summaries'].keys())
    if not sorted_frames:
        print("No frame summaries found.")
        return

    print(f"\n{'Frame':>6} {'Time':>6} {'Awake':>7} {'MaxLinV':>8} {'MaxAngV':>8} {'LowestY':>8} {'yCorr':>6}")
    for frame in sorted_frames:
        s = data['summaries'][frame]
        print(f"F{frame:>5} {s['t']:>5.2f}s {s['awake']:>2}/{s['total']:>2}  "
              f"{s['maxLinVel']:>8.3f} {s['maxAngVel']:>8.3f} {s['lowestY']:>8.3f} {s['yCorr']:>6.3f}")

    # Find settling time
    for frame in sorted_frames:
        s = data['summaries'][frame]
        if s['maxLinVel'] < 0.01 and s['maxAngVel'] < 0.05:
            print(f"\nRagdoll settled at F{frame} (t={s['t']:.2f}s)")
            break


def analyze_body_displacement(data):
    """Check total displacement of each bone from init to settled position."""
    print("\n" + "="*80)
    print("BONE DISPLACEMENT (init -> settled)")
    print("="*80)

    sorted_frames = sorted(data['frames'].keys())
    if not sorted_frames:
        return

    # Find settled frame
    settled_frame = sorted_frames[-1]
    for frame in sorted_frames:
        s = data['summaries'].get(frame, {})
        if s.get('maxLinVel', 999) < 0.1:
            settled_frame = frame
            break

    init_positions = {name: d['bonePos'] for name, d in data['init'].items()}
    settled_positions = {name: d['wPos'] for name, d in data['frames'][settled_frame].items()}

    print(f"\nSettled at F{settled_frame}")
    print(f"{'Bone':>12}  {'dX':>7} {'dY':>7} {'dZ':>7} {'Total':>7}  {'RotDelta':>8}")

    displacements = []
    for name in BONE_HIERARCHY:
        if name not in init_positions or name not in settled_positions:
            continue
        delta = settled_positions[name] - init_positions[name]
        dist = np.linalg.norm(delta)

        # Rotation delta
        init_rot = None
        settled_rot = None
        if name in data['init']:
            # We don't have init rotation in the same format, use first frame
            if 1 in data['frames'] and name in data['frames'][1]:
                init_rot = data['frames'][1][name]['wRot']
        if name in data['frames'][settled_frame]:
            settled_rot = data['frames'][settled_frame][name]['wRot']

        rot_delta = "N/A"
        if init_rot is not None and settled_rot is not None:
            rot_delta = f"{angle_between_quats(init_rot, settled_rot):.1f}deg"

        print(f"{BONE_LABELS.get(name, name):>12}  {delta[0]:>7.3f} {delta[1]:>7.3f} {delta[2]:>7.3f} {dist:>7.3f}  {rot_delta:>8}")
        displacements.append((name, dist, delta))

    # Check if the whole body moved uniformly (sliding) vs articulated (ragdoll-like)
    if displacements:
        dists = [d[1] for d in displacements]
        mean_dist = np.mean(dists)
        std_dist = np.std(dists)
        print(f"\nMean displacement: {mean_dist:.3f}m, Std: {std_dist:.3f}m")
        if std_dist < 0.02 and mean_dist > 0.05:
            print("WARNING: Very uniform displacement — ragdoll may be moving as a rigid block, not articulating!")
        elif std_dist > 0.1:
            print("Good: Significant articulation between bones.")


def analyze_colocated_bones(data):
    """Check for bones that are co-located (zero segment) that shouldn't be."""
    print("\n" + "="*80)
    print("CO-LOCATED BONE ANALYSIS (init)")
    print("="*80)

    issues = []
    for name, d in data['init'].items():
        if d['segHalf'] < 0.001:
            parent = BONE_HIERARCHY.get(name)
            if parent and parent in data['init']:
                parent_pos = data['init'][parent]['bonePos']
                dist = np.linalg.norm(d['bonePos'] - parent_pos)
                print(f"  {name} has segHalf=0 (co-located with parent {parent}), dist={dist:.4f}m")
                if parent != 'j_kosi' and name not in ('j_sebo_a',):
                    # j_kosi and j_sebo_a are often co-located, that's expected
                    issues.append((name, parent, dist))

    if not issues:
        print("  No unexpected co-located bones.")
    return issues


def analyze_initial_velocities(data):
    """Check which bones have high initial velocities (indicating constraint violation)."""
    print("\n" + "="*80)
    print("INITIAL VELOCITY ANALYSIS (F1)")
    print("="*80)

    if 1 not in data['frames']:
        print("No F1 data found.")
        return

    f1 = data['frames'][1]
    print(f"\n{'Bone':>12}  {'LinSpeed':>8}  {'AngSpeed':>8}  Status")

    high_vel_bones = []
    for name in BONE_HIERARCHY:
        if name not in f1:
            continue
        lin_speed = np.linalg.norm(f1[name]['linV'])
        ang_speed = np.linalg.norm(f1[name]['angV'])

        status = "OK"
        if ang_speed > 5.0:
            status = "HIGH ANG"
        elif ang_speed > 2.0:
            status = "MOD ANG"
        if lin_speed > 1.0:
            status = "HIGH LIN"

        flag = " <<<" if "HIGH" in status else ""
        print(f"{BONE_LABELS.get(name, name):>12}  {lin_speed:>8.3f}  {ang_speed:>8.3f}  {status}{flag}")

        if "HIGH" in status:
            high_vel_bones.append((name, lin_speed, ang_speed))

    if high_vel_bones:
        print(f"\n{len(high_vel_bones)} bones with high initial velocity — likely constraint fighting or bad initial config.")
    return high_vel_bones


def analyze_final_pose_plausibility(data):
    """Check if the final resting pose is physically plausible."""
    print("\n" + "="*80)
    print("FINAL POSE PLAUSIBILITY")
    print("="*80)

    sorted_frames = sorted(data['frames'].keys())
    if not sorted_frames:
        return

    settled_frame = sorted_frames[-1]
    for frame in sorted_frames:
        s = data['summaries'].get(frame, {})
        if s.get('maxLinVel', 999) < 0.1:
            settled_frame = frame
            break

    bones = data['frames'][settled_frame]
    ground_y = data['ground_y']

    # 1. Check if body is face-up or face-down
    if 'j_kao' in bones and 'j_kosi' in bones:
        head_y = bones['j_kao']['wPos'][1]
        pelvis_y = bones['j_kosi']['wPos'][1]
        head_above_pelvis = head_y > pelvis_y
        print(f"Head Y={head_y:.3f}, Pelvis Y={pelvis_y:.3f} -> Head {'above' if head_above_pelvis else 'BELOW'} pelvis")

    # 2. Check overall height of the body above ground
    if ground_y is not None:
        all_y = [bones[name]['wPos'][1] for name in bones]
        max_y = max(all_y)
        min_y = min(all_y)
        height_above_ground = max_y - ground_y
        print(f"Body height range: [{min_y:.3f}, {max_y:.3f}], ground={ground_y:.3f}")
        print(f"Highest point {height_above_ground:.3f}m above ground")

        if height_above_ground > 0.5:
            print("WARNING: Body is floating significantly above ground!")
        elif height_above_ground < 0.05:
            print("Body is resting very close to ground level — looks correct for a death pose.")

    # 3. Check if left/right symmetry is roughly preserved
    sym_pairs = [
        ('j_ude_a_l', 'j_ude_a_r'), ('j_ude_b_l', 'j_ude_b_r'),
        ('j_te_l', 'j_te_r'), ('j_asi_a_l', 'j_asi_a_r'),
        ('j_asi_b_l', 'j_asi_b_r'), ('j_asi_c_l', 'j_asi_c_r'),
    ]
    print(f"\nLeft/Right bone height differences at settled (F{settled_frame}):")
    for left, right in sym_pairs:
        if left in bones and right in bones:
            dy = abs(bones[left]['wPos'][1] - bones[right]['wPos'][1])
            label_l = BONE_LABELS.get(left, left)
            label_r = BONE_LABELS.get(right, right)
            flag = " <<<" if dy > 0.15 else ""
            print(f"  {label_l} vs {label_r}: dY={dy:.3f}m{flag}")

    # 4. Check if spine is roughly continuous (no wild bends)
    spine_bones = ['j_kosi', 'j_sebo_a', 'j_sebo_b', 'j_sebo_c', 'j_kubi', 'j_kao']
    print(f"\nSpine continuity check:")
    for i in range(len(spine_bones) - 1):
        b1, b2 = spine_bones[i], spine_bones[i+1]
        if b1 in bones and b2 in bones:
            dist = np.linalg.norm(bones[b2]['wPos'] - bones[b1]['wPos'])
            print(f"  {BONE_LABELS[b1]:>10} -> {BONE_LABELS[b2]:>10}: {dist:.3f}m")


def visualize(data, output_dir):
    """Generate 3D skeleton visualizations at key frames."""
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        from mpl_toolkits.mplot3d import Axes3D
    except ImportError:
        print("\nmatplotlib not available, skipping visualization.")
        return

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    sorted_frames = sorted(data['frames'].keys())
    # Pick key frames: first, one during fall, settled, last
    key_frames = []
    if sorted_frames:
        key_frames.append(sorted_frames[0])
        for f in sorted_frames:
            if f not in key_frames:
                key_frames.append(f)
                break
        # Mid-fall
        for f in sorted_frames:
            s = data['summaries'].get(f, {})
            if s.get('t', 0) >= 0.4 and f not in key_frames:
                key_frames.append(f)
                break
        # Settled
        for f in sorted_frames:
            s = data['summaries'].get(f, {})
            if s.get('maxLinVel', 999) < 0.1 and f not in key_frames:
                key_frames.append(f)
                break

    ground_y = data['ground_y']

    # --- Plot 1: Multi-frame skeleton comparison (top-down XZ and side YZ) ---
    fig, axes = plt.subplots(1, 3, figsize=(20, 7))

    # Compute bounds from all frames
    all_positions = []
    for frame in key_frames:
        for name, bd in data['frames'][frame].items():
            all_positions.append(bd['wPos'])
    if data['init']:
        for name, d in data['init'].items():
            all_positions.append(d['bonePos'])
    all_positions = np.array(all_positions)

    # Relative to pelvis init position for readability
    origin = data['init']['j_kosi']['bonePos'] if 'j_kosi' in data['init'] else all_positions.mean(axis=0)

    colors = plt.cm.viridis(np.linspace(0, 1, len(key_frames) + 1))

    # Add init skeleton
    frame_labels = ['Init'] + [f'F{f}' for f in key_frames]
    frame_positions_list = []

    # Init positions
    init_pos = {}
    for name, d in data['init'].items():
        init_pos[name] = d['bonePos'] - origin
    frame_positions_list.append(init_pos)

    for frame in key_frames:
        fp = {}
        for name, bd in data['frames'][frame].items():
            fp[name] = bd['wPos'] - origin
        frame_positions_list.append(fp)

    view_configs = [
        ('XZ (top-down)', 0, 2, 'X', 'Z'),    # top view
        ('XY (front)',     0, 1, 'X', 'Y'),    # front view
        ('ZY (side)',      2, 1, 'Z', 'Y'),    # side view
    ]

    for ax_idx, (title, dim1, dim2, xlabel, ylabel) in enumerate(view_configs):
        ax = axes[ax_idx]
        for fi, (frame_label, positions) in enumerate(zip(frame_labels, frame_positions_list)):
            color = colors[fi]
            alpha = 0.3 if fi == 0 else 0.6 + 0.4 * fi / len(frame_labels)

            # Draw connections
            for child, parent in CONNECTIONS:
                if child in positions and parent in positions:
                    cp = positions[child]
                    pp = positions[parent]
                    ax.plot([cp[dim1], pp[dim1]], [cp[dim2], pp[dim2]],
                            color=color, alpha=alpha, linewidth=1.5)

            # Draw joints
            for name, pos in positions.items():
                ax.scatter(pos[dim1], pos[dim2], color=color, s=20, alpha=alpha, zorder=5)

        # Draw ground line for side views
        if dim2 == 1 and ground_y is not None:
            gy_rel = ground_y - origin[1]
            ax.axhline(y=gy_rel, color='brown', linestyle='--', alpha=0.5, label=f'Ground Y={ground_y:.3f}')

        ax.set_title(title)
        ax.set_xlabel(xlabel + ' (m)')
        ax.set_ylabel(ylabel + ' (m)')
        ax.set_aspect('equal')
        ax.grid(True, alpha=0.3)

    # Legend
    legend_elements = []
    from matplotlib.lines import Line2D
    for fi, label in enumerate(frame_labels):
        legend_elements.append(Line2D([0], [0], color=colors[fi], label=label, linewidth=2))
    axes[0].legend(handles=legend_elements, loc='upper left', fontsize=8)

    fig.suptitle('Ragdoll Skeleton at Key Frames (relative to init pelvis)', fontsize=14)
    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_skeleton_views.png', dpi=150)
    plt.close()
    print(f"\nSaved: {output_dir / 'ragdoll_skeleton_views.png'}")

    # --- Plot 2: Velocity over time ---
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(12, 8), sharex=True)

    times = []
    max_lin = []
    max_ang = []
    awake_counts = []

    for frame in sorted(data['summaries'].keys()):
        s = data['summaries'][frame]
        times.append(s['t'])
        max_lin.append(s['maxLinVel'])
        max_ang.append(s['maxAngVel'])
        awake_counts.append(s['awake'])

    ax1.plot(times, max_lin, 'b-o', markersize=3, label='Max Linear Vel')
    ax1.plot(times, max_ang, 'r-o', markersize=3, label='Max Angular Vel')
    ax1.set_ylabel('Velocity (m/s or rad/s)')
    ax1.set_yscale('log')
    ax1.legend()
    ax1.grid(True, alpha=0.3)
    ax1.set_title('Ragdoll Settling Profile')

    ax2.plot(times, awake_counts, 'g-o', markersize=3)
    ax2.set_xlabel('Time (s)')
    ax2.set_ylabel('Awake Bodies')
    ax2.set_ylim(-0.5, 20)
    ax2.grid(True, alpha=0.3)

    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_velocity_profile.png', dpi=150)
    plt.close()
    print(f"Saved: {output_dir / 'ragdoll_velocity_profile.png'}")

    # --- Plot 3: Per-bone Y position over time (shows falling trajectory) ---
    fig, ax = plt.subplots(1, 1, figsize=(14, 8))

    bone_names = list(BONE_HIERARCHY.keys())
    bone_colors = plt.cm.tab20(np.linspace(0, 1, len(bone_names)))

    for bi, name in enumerate(bone_names):
        ts = []
        ys = []
        for frame in sorted(data['frames'].keys()):
            if name in data['frames'][frame]:
                s = data['summaries'].get(frame, {})
                t = s.get('t', frame / 60.0)
                ts.append(t)
                ys.append(data['frames'][frame][name]['wPos'][1])
        if ts:
            ax.plot(ts, ys, color=bone_colors[bi], label=BONE_LABELS.get(name, name),
                    linewidth=1.5, alpha=0.8)

    if ground_y is not None:
        ax.axhline(y=ground_y, color='brown', linestyle='--', linewidth=2, alpha=0.7, label=f'Ground ({ground_y:.3f})')

    ax.set_xlabel('Time (s)')
    ax.set_ylabel('Y Position (world)')
    ax.set_title('Bone Y Positions Over Time')
    ax.legend(loc='upper right', fontsize=7, ncol=2)
    ax.grid(True, alpha=0.3)

    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_bone_y_over_time.png', dpi=150)
    plt.close()
    print(f"Saved: {output_dir / 'ragdoll_bone_y_over_time.png'}")

    # --- Plot 4: 3D skeleton at init vs settled ---
    fig = plt.figure(figsize=(14, 7))

    for subplot_idx, (frame_label, positions) in enumerate([
        ('Init', {name: d['bonePos'] for name, d in data['init'].items()}),
        (f'Settled (F{key_frames[-1]})', {name: data['frames'][key_frames[-1]][name]['wPos'] for name in data['frames'][key_frames[-1]]}),
    ]):
        ax = fig.add_subplot(1, 2, subplot_idx + 1, projection='3d')

        for child, parent in CONNECTIONS:
            if child in positions and parent in positions:
                cp = positions[child] - origin
                pp = positions[parent] - origin
                ax.plot([cp[0], pp[0]], [cp[2], pp[2]], [cp[1], pp[1]],
                        'b-', linewidth=2, alpha=0.7)

        for name, pos in positions.items():
            p = pos - origin
            ax.scatter(p[0], p[2], p[1], c='red', s=30, zorder=5)
            ax.text(p[0], p[2], p[1], f' {BONE_LABELS.get(name, name)}', fontsize=6)

        if ground_y is not None:
            gy = ground_y - origin[1]
            xx, zz = np.meshgrid(np.linspace(-0.5, 0.5, 2), np.linspace(-0.5, 0.5, 2))
            ax.plot_surface(xx, zz, np.full_like(xx, gy), alpha=0.15, color='brown')

        ax.set_xlabel('X')
        ax.set_ylabel('Z')
        ax.set_zlabel('Y (up)')
        ax.set_title(frame_label)

    plt.suptitle('3D Ragdoll Pose Comparison', fontsize=14)
    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_3d_comparison.png', dpi=150)
    plt.close()
    print(f"Saved: {output_dir / 'ragdoll_3d_comparison.png'}")


def main():
    if len(sys.argv) < 2:
        log_path = Path(__file__).parent.parent / 'temp' / 'ragdoll_log.txt'
    else:
        log_path = Path(sys.argv[1])

    if not log_path.exists():
        print(f"Log file not found: {log_path}")
        sys.exit(1)

    print(f"Parsing: {log_path}")
    data = parse_log(log_path)

    print(f"\nParsed: {len(data['init'])} init bones, {len(data['frames'])} frames, {len(data['summaries'])} summaries")
    print(f"Skeleton pos: {data['skel_pos']}")
    print(f"Skeleton rot: {data['skel_rot']}")
    print(f"Ground Y: {data['ground_y']}")

    # Run analyses
    seg_issues = analyze_segment_lengths(data)
    colocated_issues = analyze_colocated_bones(data)
    ground_issues = analyze_ground_penetration(data)
    analyze_velocity_profile(data)
    high_vel_bones = analyze_initial_velocities(data)
    analyze_body_displacement(data)
    analyze_final_pose_plausibility(data)

    # Summary of issues
    print("\n" + "="*80)
    print("ISSUE SUMMARY")
    print("="*80)

    all_issues = []
    if seg_issues:
        all_issues.append(f"{len(seg_issues)} segment length issues")
    if colocated_issues:
        all_issues.append(f"{len(colocated_issues)} unexpected co-located bones")
    if ground_issues:
        all_issues.append(f"{len(ground_issues)} ground penetration events")
    if high_vel_bones:
        all_issues.append(f"{len(high_vel_bones)} bones with high initial velocity")

    if all_issues:
        for issue in all_issues:
            print(f"  - {issue}")
    else:
        print("  No significant issues detected.")

    # Visualize
    output_dir = Path(__file__).parent.parent / 'temp'
    visualize(data, output_dir)

    print("\nDone.")


if __name__ == '__main__':
    main()
