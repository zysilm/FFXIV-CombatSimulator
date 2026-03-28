"""
Evaluate settled ragdoll pose "humanity" score.
Produces numeric metrics that correlate with visual quality.

Usage: python tools/evaluate_pose.py temp/ragdoll_log.txt
"""
import re, sys, math
import numpy as np
from pathlib import Path

BONE_HIERARCHY = {
    'j_kosi': None, 'j_sebo_a': 'j_kosi', 'j_sebo_b': 'j_sebo_a',
    'j_sebo_c': 'j_sebo_b', 'j_kubi': 'j_sebo_c', 'j_kao': 'j_kubi',
    'j_ude_a_l': 'j_sebo_c', 'j_ude_a_r': 'j_sebo_c',
    'j_ude_b_l': 'j_ude_a_l', 'j_ude_b_r': 'j_ude_a_r',
    'j_te_l': 'j_ude_b_l', 'j_te_r': 'j_ude_b_r',
    'j_asi_a_l': 'j_kosi', 'j_asi_a_r': 'j_kosi',
    'j_asi_b_l': 'j_asi_a_l', 'j_asi_b_r': 'j_asi_a_r',
    'j_asi_c_l': 'j_asi_b_l', 'j_asi_c_r': 'j_asi_b_r',
}

LABELS = {
    'j_kosi': 'Pelvis', 'j_sebo_a': 'L.Spine', 'j_sebo_b': 'M.Spine',
    'j_sebo_c': 'Chest', 'j_kubi': 'Neck', 'j_kao': 'Head',
    'j_ude_a_l': 'L.Shld', 'j_ude_a_r': 'R.Shld',
    'j_ude_b_l': 'L.Elbow', 'j_ude_b_r': 'R.Elbow',
    'j_te_l': 'L.Hand', 'j_te_r': 'R.Hand',
    'j_asi_a_l': 'L.Hip', 'j_asi_a_r': 'R.Hip',
    'j_asi_b_l': 'L.Knee', 'j_asi_b_r': 'R.Knee',
    'j_asi_c_l': 'L.Foot', 'j_asi_c_r': 'R.Foot',
}

SYM_PAIRS = [
    ('j_ude_a_l', 'j_ude_a_r'), ('j_ude_b_l', 'j_ude_b_r'),
    ('j_te_l', 'j_te_r'), ('j_asi_a_l', 'j_asi_a_r'),
    ('j_asi_b_l', 'j_asi_b_r'), ('j_asi_c_l', 'j_asi_c_r'),
]

# Bones that form hinge chains: (parent, joint, child) for angle measurement
HINGE_CHAINS = [
    ('j_asi_a_l', 'j_asi_b_l', 'j_asi_c_l', 'L.Knee'),
    ('j_asi_a_r', 'j_asi_b_r', 'j_asi_c_r', 'R.Knee'),
    ('j_ude_a_l', 'j_ude_b_l', 'j_te_l', 'L.Elbow'),
    ('j_ude_a_r', 'j_ude_b_r', 'j_te_r', 'R.Elbow'),
]


def parse_vec3(s):
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:3]])

def parse_quat(s):
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:4]])

def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
        [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
        [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]
    ])

def angle_between(v1, v2):
    """Angle in degrees between two vectors."""
    c = np.dot(v1, v2) / (np.linalg.norm(v1) * np.linalg.norm(v2) + 1e-10)
    return math.degrees(math.acos(np.clip(c, -1, 1)))


def parse_log(filepath):
    """Parse log, return init bones, settled frame bones, ground_y, and skel_rot."""
    init_bones = {}
    all_frames = {}
    ground_y = None
    skel_rot = None

    with open(filepath) as f:
        for line in f:
            line = line.strip()

            m = re.search(r'Skeleton transform pos=\([^)]+\) rot=\(([^)]+)\)', line)
            if m:
                skel_rot = parse_quat(m.group(1))

            m = re.search(r'Raycast ground Y=([-\d.]+)', line)
            if m:
                ground_y = float(m.group(1))

            m = re.search(
                r"\[Ragdoll Init\] '(\w+)' idx=(\d+) "
                r"bonePos=\(([^)]+)\) capsuleCenter=\(([^)]+)\) "
                r"segHalf=([\d.]+) capsuleLen=([\d.]+)",
                line)
            if m:
                init_bones[m.group(1)] = {
                    'bonePos': parse_vec3(m.group(3)),
                    'segHalf': float(m.group(5)),
                }

            m = re.search(
                r"\[Ragdoll F(\d+)\] '(\w+)' "
                r"wPos=\(([^)]+)\) wRot=\(([^)]+)\) "
                r"linV=\(([^)]+)\) angV=\(([^)]+)\) "
                r"awake=(\w+)", line)
            if m:
                frame = int(m.group(1))
                name = m.group(2)
                if frame not in all_frames:
                    all_frames[frame] = {}
                all_frames[frame][name] = {
                    'wPos': parse_vec3(m.group(3)),
                    'wRot': parse_quat(m.group(4)),
                    'linV': parse_vec3(m.group(5)),
                    'angV': parse_vec3(m.group(6)),
                    'awake': m.group(7) == 'True',
                }

    # Find settled frame: last frame with full 18 bones
    settled = None
    for frame in sorted(all_frames.keys(), reverse=True):
        if len(all_frames[frame]) >= 18:
            settled = all_frames[frame]
            break

    return init_bones, settled, ground_y, skel_rot


def evaluate(init_bones, settled, ground_y, skel_rot=None):
    """Compute humanity metrics. Returns dict of scores (0=perfect, higher=worse)."""
    if not settled or not ground_y:
        return {}

    pos = {name: bd['wPos'] for name, bd in settled.items()}
    rot = {name: bd['wRot'] for name, bd in settled.items()}

    results = {}
    penalties = {}

    # ===== 1. HEIGHT SPREAD =====
    # A body lying flat should have small Y spread (~body thickness, <0.15m)
    all_y = [pos[n][1] for n in pos]
    y_spread = max(all_y) - min(all_y)
    results['height_spread'] = y_spread
    # Penalty: anything above 0.12m is suspicious
    penalties['height_spread'] = max(0, (y_spread - 0.12) / 0.05) * 15

    # ===== 2. BONES ABOVE PELVIS =====
    # When lying down, no bone should be significantly above pelvis
    pelvis_y = pos.get('j_kosi', np.zeros(3))[1]
    above_pelvis = {}
    above_penalty = 0
    for name in pos:
        if name == 'j_kosi':
            continue
        dy = pos[name][1] - pelvis_y
        if dy > 0.03:  # 3cm threshold
            above_pelvis[name] = dy
            above_penalty += dy * 100  # 10 points per cm above
    results['bones_above_pelvis'] = above_pelvis
    penalties['bones_above_pelvis'] = min(above_penalty, 30)

    # ===== 3. GROUND PROXIMITY =====
    # Extremities and pelvis should be near ground (within ~0.15m)
    ground_bones = ['j_kosi', 'j_te_l', 'j_te_r', 'j_asi_c_l', 'j_asi_c_r', 'j_kao']
    ground_gaps = {}
    ground_penalty = 0
    for name in ground_bones:
        if name not in pos:
            continue
        gap = pos[name][1] - ground_y
        ground_gaps[name] = gap
        if gap > 0.15:
            ground_penalty += (gap - 0.15) * 50
    results['ground_gaps'] = ground_gaps
    penalties['ground_proximity'] = min(ground_penalty, 20)

    # ===== 4. JOINT ANGLES =====
    # Check knee/elbow angles. For lying pose, knees shouldn't be hyper-bent
    # or hyper-extended. Measure angle at joint between parent and child segments.
    joint_angles = {}
    joint_penalty = 0
    for parent_name, joint_name, child_name, label in HINGE_CHAINS:
        if parent_name not in pos or joint_name not in pos or child_name not in pos:
            continue
        v1 = pos[parent_name] - pos[joint_name]  # joint -> parent
        v2 = pos[child_name] - pos[joint_name]    # joint -> child
        angle = angle_between(v1, v2)
        joint_angles[label] = angle
        # Very small angles (<20°) = hyper-folded. Very large (>170°) = hyper-extended.
        # Both look unnatural at rest.
        if angle < 20:
            joint_penalty += (20 - angle) * 0.5
        # Slight bend is OK, straight is OK, but >160° might be hyperextension
        # For knees at rest when lying down, 90-180° is normal

    results['joint_angles'] = joint_angles
    penalties['joint_angles'] = min(joint_penalty, 15)

    # ===== 4b. KNEE BEND DIRECTION =====
    # Derive "forward" from the body's own geometry (spine direction),
    # not from skeleton rotation convention or init pose comparison.
    #
    # For each knee:
    #   body_axis = normalize(neck - pelvis) — spine direction
    #   hinge_axis = cross(thigh_dir, body_axis) — lateral axis
    #   forward = cross(hinge_axis, thigh_dir) — "forward" in bend plane
    #   If dot(shin, forward) > 0 → shin goes forward = hyperextension
    knee_bend = {}
    knee_bend_penalty = 0
    knee_chains = [
        ('j_asi_a_l', 'j_asi_b_l', 'j_asi_c_l', 'L.Knee'),
        ('j_asi_a_r', 'j_asi_b_r', 'j_asi_c_r', 'R.Knee'),
    ]

    # Body axis from spine (pelvis → neck/chest area)
    body_axis = np.zeros(3)
    if 'j_kosi' in pos and 'j_kubi' in pos:
        body_axis = pos['j_kubi'] - pos['j_kosi']
        ba_len = np.linalg.norm(body_axis)
        if ba_len > 0.01:
            body_axis = body_axis / ba_len

    for hip_name, knee_name, foot_name, label in knee_chains:
        if hip_name not in pos or knee_name not in pos or foot_name not in pos:
            continue

        thigh = pos[knee_name] - pos[hip_name]
        shin = pos[foot_name] - pos[knee_name]
        thigh_n = thigh / (np.linalg.norm(thigh) + 1e-10)
        shin_n = shin / (np.linalg.norm(shin) + 1e-10)
        angle = joint_angles.get(label, 180)

        # Compute "forward" in the knee's bend plane using body geometry
        hinge_axis = np.cross(thigh_n, body_axis)
        hinge_len = np.linalg.norm(hinge_axis)

        direction = 'OK'
        fwd_dot = 0.0

        if hinge_len > 0.1 and angle < 160:
            hinge_axis = hinge_axis / hinge_len

            # cross(thigh, body_axis) points LEFT for right leg, RIGHT for left leg.
            # Ensure hinge_axis consistently points to the character's LEFT by checking
            # which side this leg is on: if hip is to the LEFT of pelvis (in the
            # hinge_axis direction), this is the left leg and we need to flip.
            hip_offset = pos[hip_name] - pos['j_kosi']
            if np.dot(hip_offset, hinge_axis) > 0:
                hinge_axis = -hinge_axis  # flip so it always points character-left

            forward = np.cross(hinge_axis, thigh_n)
            forward = forward / (np.linalg.norm(forward) + 1e-10)
            fwd_dot = float(np.dot(shin_n, forward))

            # Positive = shin going forward = hyperextension
            if fwd_dot > 0.15:
                direction = 'HYPEREXTENDED'
                knee_bend_penalty += 15

        knee_bend[label] = {
            'fwd_dot': fwd_dot,
            'direction': direction,
            'angle': angle,
        }

    results['knee_bend_direction'] = knee_bend
    penalties['knee_bend_dir'] = min(knee_bend_penalty, 30)

    # ===== 5. THIGH ELEVATION ANGLE =====
    # When lying down, thighs should be roughly horizontal (along XZ plane).
    # A thigh pointing upward (large Y component in hip->knee direction) is wrong.
    thigh_angles = {}
    thigh_penalty = 0
    for hip, knee, label in [('j_asi_a_l', 'j_asi_b_l', 'L.Thigh'),
                              ('j_asi_a_r', 'j_asi_b_r', 'R.Thigh')]:
        if hip not in pos or knee not in pos:
            continue
        thigh_dir = pos[knee] - pos[hip]
        thigh_len = np.linalg.norm(thigh_dir)
        if thigh_len < 0.01:
            continue
        thigh_dir_n = thigh_dir / thigh_len
        # Angle from horizontal (Y=0 plane). 0°=horizontal, 90°=vertical
        elev = math.degrees(math.asin(abs(thigh_dir_n[1])))
        # Also check if the hip bone itself is elevated above pelvis
        hip_above = pos[hip][1] - pelvis_y
        thigh_angles[label] = {
            'elevation': elev, 'dir_y': thigh_dir_n[1],
            'length': thigh_len, 'hip_above_pelvis': hip_above
        }
        # Penalty: thigh going upward (positive Y from hip to knee)
        if thigh_dir_n[1] > 0:  # pointing up
            thigh_penalty += elev * 0.5
        elif elev > 45:  # pointing steeply down is also a bit odd at rest
            thigh_penalty += (elev - 45) * 0.2
        # Penalty: hip bone elevated above pelvis (thigh sticking up from body)
        if hip_above > 0.03:
            thigh_penalty += hip_above * 150  # strong penalty

    results['thigh_elevation'] = thigh_angles
    penalties['thigh_elevation'] = min(thigh_penalty, 20)

    # ===== 6. SEGMENT STRETCH =====
    # Compare init vs settled parent-child distances. Large changes = constraint violation.
    stretch_issues = {}
    stretch_penalty = 0
    for child, parent in BONE_HIERARCHY.items():
        if parent is None:
            continue
        if child not in pos or parent not in pos:
            continue
        if child not in init_bones or parent not in init_bones:
            continue
        init_dist = np.linalg.norm(init_bones[child]['bonePos'] - init_bones[parent]['bonePos'])
        settled_dist = np.linalg.norm(pos[child] - pos[parent])
        if init_dist < 0.01:
            continue
        stretch = abs(settled_dist - init_dist) / init_dist
        if stretch > 0.05:  # >5% change
            stretch_issues[f"{LABELS.get(parent,'?')}->{LABELS.get(child,'?')}"] = {
                'init': init_dist, 'settled': settled_dist,
                'stretch_pct': stretch * 100
            }
            stretch_penalty += stretch * 30

    results['segment_stretch'] = stretch_issues
    penalties['segment_stretch'] = min(stretch_penalty, 20)

    # ===== 7. LEFT-RIGHT HEIGHT ASYMMETRY =====
    asym_issues = {}
    asym_penalty = 0
    for left, right in SYM_PAIRS:
        if left not in pos or right not in pos:
            continue
        dy = abs(pos[left][1] - pos[right][1])
        label = f"{LABELS[left]} vs {LABELS[right]}"
        asym_issues[label] = dy
        if dy > 0.05:
            asym_penalty += (dy - 0.05) * 80

    results['lr_asymmetry'] = asym_issues
    penalties['lr_asymmetry'] = min(asym_penalty, 15)

    # ===== FINAL SCORE =====
    total_penalty = sum(penalties.values())
    humanity_score = max(0, 100 - total_penalty)

    return {
        'score': humanity_score,
        'penalties': penalties,
        'details': results,
    }


def print_report(eval_result):
    """Print formatted evaluation report."""
    score = eval_result['score']
    penalties = eval_result['penalties']
    details = eval_result['details']

    print("=" * 70)
    print(f"  HUMANITY SCORE: {score:.1f} / 100")
    print("=" * 70)

    # Penalty breakdown
    print(f"\n--- Penalty Breakdown ---")
    for name, val in sorted(penalties.items(), key=lambda x: -x[1]):
        bar = "#" * int(val) + "." * max(0, 30 - int(val))
        status = "OK" if val < 1 else "WARN" if val < 5 else "BAD"
        print(f"  {name:>20}: -{val:5.1f}  [{status}]  {bar[:30]}")

    # Height spread
    print(f"\n--- Height Spread ---")
    print(f"  Y range: {details['height_spread']:.3f}m")

    # Bones above pelvis
    if details['bones_above_pelvis']:
        print(f"\n--- Bones Above Pelvis (BAD) ---")
        for name, dy in sorted(details['bones_above_pelvis'].items(), key=lambda x: -x[1]):
            print(f"  {LABELS.get(name, name):>12}: +{dy:.3f}m above pelvis")
    else:
        print(f"\n--- Bones Above Pelvis: None (good) ---")

    # Ground proximity
    print(f"\n--- Ground Proximity ---")
    for name, gap in sorted(details['ground_gaps'].items(), key=lambda x: -x[1]):
        status = "OK" if gap < 0.15 else "HIGH"
        print(f"  {LABELS.get(name, name):>12}: {gap:.3f}m above ground  [{status}]")

    # Thigh elevation
    print(f"\n--- Thigh Elevation ---")
    for label, info in details['thigh_elevation'].items():
        direction = "UP" if info['dir_y'] > 0 else "DOWN"
        status = "BAD" if info['dir_y'] > 0 and info['elevation'] > 15 else "OK"
        print(f"  {label:>12}: {info['elevation']:.1f}° from horizontal, pointing {direction}, len={info['length']:.3f}m  [{status}]")

    # Joint angles
    print(f"\n--- Joint Angles ---")
    for label, angle in details['joint_angles'].items():
        status = "OK" if 20 < angle < 170 else "BAD"
        print(f"  {label:>12}: {angle:.1f}  [{status}]")

    # Knee bend direction
    if details.get('knee_bend_direction'):
        print(f"\n--- Knee Bend Direction ---")
        for label, info in details['knee_bend_direction'].items():
            status = info['direction']
            flag = " <<<" if status == "HYPEREXTENDED" else ""
            fwd_dot = info.get('fwd_dot', 0)
            print(f"  {label:>12}: {status} (fwd_dot={fwd_dot:+.3f}, angle={info['angle']:.1f}){flag}")

    # Segment stretch
    if details['segment_stretch']:
        print(f"\n--- Segment Stretch (>5%) ---")
        for label, info in sorted(details['segment_stretch'].items(), key=lambda x: -x[1]['stretch_pct']):
            print(f"  {label:>20}: {info['init']:.3f} -> {info['settled']:.3f}m ({info['stretch_pct']:.1f}%)")
    else:
        print(f"\n--- Segment Stretch: All within 5% (good) ---")

    # L/R Asymmetry
    print(f"\n--- L/R Height Asymmetry ---")
    for label, dy in sorted(details['lr_asymmetry'].items(), key=lambda x: -x[1]):
        status = "OK" if dy < 0.05 else "BAD"
        print(f"  {label:>25}: {dy:.3f}m  [{status}]")

    print()


def main():
    if len(sys.argv) < 2:
        log_path = Path(__file__).parent.parent / 'temp' / 'ragdoll_log.txt'
    else:
        log_path = Path(sys.argv[1])

    init_bones, settled, ground_y, skel_rot = parse_log(log_path)
    if not settled:
        print("No settled frame with 18 bones found.")
        return

    print(f"Parsed: {len(init_bones)} init bones, settled frame with {len(settled)} bones")
    print(f"Ground Y: {ground_y}")

    result = evaluate(init_bones, settled, ground_y, skel_rot)
    print_report(result)


if __name__ == '__main__':
    main()
