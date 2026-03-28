"""
Visualize settled ragdoll pose with bone orientations.
Shows bone positions, capsule axes, and local coordinate frames
to diagnose rotation mapping issues between BEPU and FFXIV.
"""
import re
import sys
import numpy as np
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d import Axes3D

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

CONNECTIONS = [(c, p) for c, p in BONE_HIERARCHY.items() if p]


def parse_vec3(s):
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:3]])

def parse_quat(s):
    nums = re.findall(r'[-+]?\d*\.?\d+', s)
    return np.array([float(x) for x in nums[:4]])

def quat_to_mat(q):
    x, y, z, w = q
    return np.array([
        [1-2*(y*y+z*z), 2*(x*y-z*w),   2*(x*z+y*w)],
        [2*(x*y+z*w),   1-2*(x*x+z*z), 2*(y*z-x*w)],
        [2*(x*z-y*w),   2*(y*z+x*w),   1-2*(x*x+y*y)]
    ])

def rotate_vec(q, v):
    return quat_to_mat(q) @ v


def parse_settled_frame(lines):
    """Parse the last frame's bone data."""
    bones = {}
    ground_y = None
    skel_pos = None
    skel_rot = None
    init_bones = {}

    for line in lines:
        line = line.strip()

        m = re.search(r'Skeleton transform pos=\(([^)]+)\) rot=\(([^)]+)\)', line)
        if m:
            skel_pos = parse_vec3(m.group(1))
            skel_rot = parse_quat(m.group(2))

        m = re.search(r'Raycast ground Y=([-\d.]+)', line)
        if m:
            ground_y = float(m.group(1))

        m = re.search(
            r"\[Ragdoll Init\] '(\w+)' idx=(\d+) "
            r"bonePos=\(([^)]+)\) capsuleCenter=\(([^)]+)\) "
            r"segHalf=([\d.]+) capsuleLen=([\d.]+) "
            r"capsuleY=\(([^)]+)\)",
            line
        )
        if m:
            name = m.group(1)
            init_bones[name] = {
                'bonePos': parse_vec3(m.group(3)),
                'capsuleCenter': parse_vec3(m.group(4)),
                'segHalf': float(m.group(5)),
                'capsuleY': parse_vec3(m.group(7)),
            }

        m = re.search(
            r"\[Ragdoll F\d+\] '(\w+)' "
            r"wPos=\(([^)]+)\) wRot=\(([^)]+)\)",
            line
        )
        if m:
            name = m.group(1)
            bones[name] = {
                'wPos': parse_vec3(m.group(2)),
                'wRot': parse_quat(m.group(3)),
            }

    return bones, ground_y, skel_pos, skel_rot, init_bones


def main():
    if len(sys.argv) < 2:
        log_path = Path(__file__).parent.parent / 'temp' / 'ragdoll_log.txt'
    else:
        log_path = Path(sys.argv[1])

    with open(log_path) as f:
        lines = f.readlines()

    bones, ground_y, skel_pos, skel_rot, init_bones = parse_settled_frame(lines)
    if not bones:
        print("No bone data found")
        return

    # Use pelvis as origin
    origin = bones.get('j_kosi', {'wPos': np.zeros(3)})['wPos']

    output_dir = Path(__file__).parent.parent / 'temp'

    # === Figure 1: 3-view with orientation arrows ===
    fig, axes = plt.subplots(1, 3, figsize=(24, 9))

    views = [
        ('XZ Top-down', 0, 2, 'X', 'Z'),
        ('XY Front', 0, 1, 'X', 'Y (up)'),
        ('ZY Side', 2, 1, 'Z', 'Y (up)'),
    ]

    axis_len = 0.06
    axis_colors = {'X': 'red', 'Y': 'green', 'Z': 'blue'}

    for ax_idx, (title, d1, d2, xl, yl) in enumerate(views):
        ax = axes[ax_idx]

        # Draw skeleton connections
        for child, parent in CONNECTIONS:
            if child in bones and parent in bones:
                cp = bones[child]['wPos'] - origin
                pp = bones[parent]['wPos'] - origin
                ax.plot([cp[d1], pp[d1]], [cp[d2], pp[d2]], 'k-', lw=1.5, alpha=0.5)

        # Draw bones with local axis indicators
        for name, bd in bones.items():
            pos = bd['wPos'] - origin
            rot = bd['wRot']

            # Joint position
            ax.scatter(pos[d1], pos[d2], c='black', s=25, zorder=10)

            # Draw local X (red), Y (green), Z (blue) axes
            for axis_idx, (axis_name, color) in enumerate(zip(['X', 'Y', 'Z'], ['red', 'green', 'blue'])):
                unit = np.zeros(3)
                unit[axis_idx] = axis_len
                tip = pos + rotate_vec(rot, unit)
                ax.annotate('', xy=(tip[d1], tip[d2]), xytext=(pos[d1], pos[d2]),
                            arrowprops=dict(arrowstyle='->', color=color, lw=1.2))

            # Label
            ax.text(pos[d1] + 0.01, pos[d2] + 0.01, LABELS.get(name, name), fontsize=6, alpha=0.7)

        # Ground line (side/front views)
        if d2 == 1 and ground_y is not None:
            ax.axhline(y=ground_y - origin[1], color='brown', ls='--', alpha=0.5, label='Ground')

        ax.set_title(f'{title}\n(R=bone X, G=bone Y, B=bone Z)')
        ax.set_xlabel(xl)
        ax.set_ylabel(yl)
        ax.set_aspect('equal')
        ax.grid(True, alpha=0.2)

    plt.suptitle('Settled Ragdoll Pose — Bone Orientations (FFXIV world coords)', fontsize=14)
    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_pose_orientations.png', dpi=150)
    plt.close()
    print(f"Saved: {output_dir / 'ragdoll_pose_orientations.png'}")

    # === Figure 2: Compare init bone positions vs settled bone positions ===
    fig, axes = plt.subplots(1, 3, figsize=(24, 9))

    for ax_idx, (title, d1, d2, xl, yl) in enumerate(views):
        ax = axes[ax_idx]

        # Init pose (grey)
        for child, parent in CONNECTIONS:
            if child in init_bones and parent in init_bones:
                cp = init_bones[child]['bonePos'] - origin
                pp = init_bones[parent]['bonePos'] - origin
                ax.plot([cp[d1], pp[d1]], [cp[d2], pp[d2]], '-', color='grey', lw=1, alpha=0.5)
        for name, ib in init_bones.items():
            pos = ib['bonePos'] - origin
            ax.scatter(pos[d1], pos[d2], c='grey', s=15, alpha=0.5, zorder=5)

        # Settled pose (blue)
        for child, parent in CONNECTIONS:
            if child in bones and parent in bones:
                cp = bones[child]['wPos'] - origin
                pp = bones[parent]['wPos'] - origin
                ax.plot([cp[d1], pp[d1]], [cp[d2], pp[d2]], 'b-', lw=2, alpha=0.8)
        for name, bd in bones.items():
            pos = bd['wPos'] - origin
            ax.scatter(pos[d1], pos[d2], c='blue', s=25, zorder=10)
            ax.text(pos[d1] + 0.01, pos[d2] + 0.01, LABELS.get(name, name), fontsize=6, color='blue')

        if d2 == 1 and ground_y is not None:
            ax.axhline(y=ground_y - origin[1], color='brown', ls='--', alpha=0.5)

        ax.set_title(f'{title} (grey=init, blue=settled)')
        ax.set_xlabel(xl)
        ax.set_ylabel(yl)
        ax.set_aspect('equal')
        ax.grid(True, alpha=0.2)

    plt.suptitle('Init vs Settled Pose Comparison', fontsize=14)
    plt.tight_layout()
    plt.savefig(output_dir / 'ragdoll_init_vs_settled.png', dpi=150)
    plt.close()
    print(f"Saved: {output_dir / 'ragdoll_init_vs_settled.png'}")

    # === Print analysis: segment lengths and bone Y (up) directions ===
    print("\n=== BONE ORIENTATION ANALYSIS (settled) ===")
    print(f"{'Bone':>12}  {'Y-up':>6}  {'BoneY dir':>24}  {'Segment to child':>24}")
    for name in BONE_HIERARCHY:
        if name not in bones:
            continue
        rot = bones[name]['wRot']
        # Local Y axis in world space (capsule length axis)
        local_y_world = rotate_vec(rot, np.array([0, 1, 0]))
        # How much of local Y points world-up?
        y_up = local_y_world[1]

        # Segment direction to first child
        seg_dir = ""
        for child, parent in BONE_HIERARCHY.items():
            if parent == name and child in bones:
                delta = bones[child]['wPos'] - bones[name]['wPos']
                dist = np.linalg.norm(delta)
                if dist > 0.01:
                    seg_dir = f"({delta[0]/dist:+.2f},{delta[1]/dist:+.2f},{delta[2]/dist:+.2f}) d={dist:.3f}"
                else:
                    seg_dir = f"co-located d={dist:.4f}"
                break

        print(f"{LABELS.get(name,name):>12}  {y_up:+.3f}  ({local_y_world[0]:+.3f},{local_y_world[1]:+.3f},{local_y_world[2]:+.3f})  {seg_dir}")

    # === Check: capsule clamping damage report ===
    print("\n=== CAPSULE CLAMPING REPORT ===")
    print("Bones where capsule was aggressively shrunk (segHalf clamped to 0.01):")
    for name, ib in init_bones.items():
        if ib['segHalf'] <= 0.01 and ib['segHalf'] > 0:
            parent = BONE_HIERARCHY.get(name)
            if parent and parent in init_bones:
                parent_pos = init_bones[parent]['bonePos']
                seg_dist = np.linalg.norm(ib['bonePos'] - parent_pos)
                print(f"  {LABELS.get(name,name):>12}: segHalf=0.010, actual segment={seg_dist:.3f}m  <<< TINY CAPSULE")

    print("\nDone.")


if __name__ == '__main__':
    main()
