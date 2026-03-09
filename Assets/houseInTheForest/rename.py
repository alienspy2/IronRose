#!/usr/bin/env python3
"""
Rename script for 3D model files in subdirectories.
- model.glb -> {folder_name}.glb
- model.obj -> {folder_name}.obj
- preview.png -> {folder_name}_preview.png
"""

import os
import sys


def rename_files_in_folder(folder_path):
    """Rename files in a single folder."""
    folder_name = os.path.basename(folder_path)

    files_renamed = []
    errors = []

    # Define rename mappings (files and their corresponding .rose files)
    rename_map = {
        "model.glb": f"{folder_name}.glb",
        "model.glb.rose": f"{folder_name}.glb.rose",
        "model.obj": f"{folder_name}.obj",
        "model.obj.rose": f"{folder_name}.obj.rose",
        "preview.png": f"{folder_name}_preview.png",
        "preview.png.rose": f"{folder_name}_preview.png.rose",
    }

    for old_name, new_name in rename_map.items():
        old_path = os.path.join(folder_path, old_name)
        new_path = os.path.join(folder_path, new_name)

        if os.path.exists(old_path):
            if os.path.exists(new_path):
                errors.append(f"Target file already exists: {new_path}")
                continue

            try:
                os.rename(old_path, new_path)
                files_renamed.append(f"{old_name} -> {new_name}")
            except Exception as e:
                errors.append(f"Error renaming {old_name}: {e}")

    return files_renamed, errors


def main():
    """Main function to process all subdirectories."""
    current_dir = os.getcwd()

    print(f"Scanning directories in: {current_dir}")
    print("-" * 60)

    total_renamed = 0
    total_errors = 0
    folders_processed = 0

    # Get all subdirectories
    try:
        items = os.listdir(current_dir)
    except Exception as e:
        print(f"Error listing directory: {e}")
        sys.exit(1)

    folders = [item for item in items if os.path.isdir(os.path.join(current_dir, item))]

    if not folders:
        print("No subdirectories found.")
        return

    print(f"Found {len(folders)} subdirectories to process\n")

    for folder in sorted(folders):
        folder_path = os.path.join(current_dir, folder)

        files_renamed, errors = rename_files_in_folder(folder_path)

        if files_renamed or errors:
            print(f"\n📁 {folder}/")
            folders_processed += 1

            for rename in files_renamed:
                print(f"   ✓ {rename}")
                total_renamed += 1

            for error in errors:
                print(f"   ✗ {error}")
                total_errors += 1

    print("\n" + "=" * 60)
    print(f"Folders processed: {folders_processed}")
    print(f"Files renamed: {total_renamed}")
    if total_errors > 0:
        print(f"Errors: {total_errors}")
    print("=" * 60)


if __name__ == "__main__":
    main()
