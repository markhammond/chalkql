#!/usr/bin/env python3
"""
Generate direct third-party PackageReference unions for the two wide ChalkQL packages.

Why generate these?
-------------------
The public packages contain several assemblies each. NuGet's pack target does not flatten
PackageReferences from those constituent projects automatically, and publishing the old
per-assembly packages merely to preserve that graph would defeat the purpose of the wide
packages.

The script reads each already-restored project's obj/project.assets.json and copies only
its *direct* PackageReference requirements into generated MSBuild .props files.

It deliberately:
  * ignores ProjectReference dependencies;
  * skips dependencies whose assets file marks suppressParent=All (normally PrivateAssets=all);
  * preserves requested version ranges rather than pinning resolved third-party versions;
  * fails when two constituent projects declare materially different requirements for the
    same package, rather than silently choosing one.

Run after `dotnet build`/restore and before restoring the packaging projects.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from xml.sax.saxutils import quoteattr

TFM = "net10.0"

GROUPS = {
    "ChalkQL": [
        "Chalk",
        "Chalk.Catalog",
        "Chalk.Client",
        "Chalk.Entitlements",
        "Chalk.Entitlements.Tenancy",
        "Chalk.Execution",
        "Chalk.Ir",
        "Chalk.Sources.Abstractions",
    ],
    "ChalkQL.Sources": [
        "Chalk.Sources.Ado",
        "Chalk.Sources.Conformance",
        "Chalk.Sources.DuckDb",
        "Chalk.Sources.Poco",
    ],
}


def normalise_assets(value: object) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    # project.assets.json commonly serialises asset sets comma-separated.
    return ";".join(part.strip() for part in text.split(",") if part.strip())


def framework_node(data: dict, path: Path) -> dict:
    frameworks = data.get("project", {}).get("frameworks", {})
    if TFM in frameworks:
        return frameworks[TFM]

    # Be slightly forgiving if the SDK serialises a long-form framework key.
    candidates = [
        (name, node)
        for name, node in frameworks.items()
        if name.lower() == TFM.lower() or "v10.0" in name.lower()
    ]
    if len(candidates) == 1:
        return candidates[0][1]

    raise SystemExit(
        f"{path}: could not identify {TFM!r}; available frameworks: "
        + ", ".join(frameworks.keys())
    )


def direct_dependencies(assets_path: Path) -> dict[str, tuple[str, str | None, str | None, str | None]]:
    data = json.loads(assets_path.read_text(encoding="utf-8"))
    framework = framework_node(data, assets_path)

    result: dict[str, tuple[str, str | None, str | None, str | None]] = {}
    for package, spec in framework.get("dependencies", {}).items():
        if not isinstance(spec, dict) or str(spec.get("target", "")).lower() != "package":
            continue

        suppress_parent = normalise_assets(spec.get("suppressParent"))
        if suppress_parent and suppress_parent.lower() == "all":
            continue

        version = spec.get("version")
        if not version:
            raise SystemExit(f"{assets_path}: direct package {package!r} has no requested version")

        result[package] = (
            str(version),
            normalise_assets(spec.get("include")),
            normalise_assets(spec.get("exclude")),
            suppress_parent,
        )
    return result


def merge_dependencies(root: Path, projects: list[str]) -> dict[str, tuple[str, str | None, str | None, str | None]]:
    merged: dict[str, tuple[str, str | None, str | None, str | None]] = {}
    origin: dict[str, str] = {}

    for project in projects:
        assets = root / "dotnet" / "src" / project / "obj" / "project.assets.json"
        if not assets.exists():
            raise SystemExit(
                f"Missing {assets}. Build/restore dotnet/Chalk.slnx before generating package dependencies."
            )

        for package, requirement in direct_dependencies(assets).items():
            previous = merged.get(package)
            if previous is not None and previous != requirement:
                raise SystemExit(
                    f"Conflicting direct PackageReference requirements for {package}:\n"
                    f"  {origin[package]}: {previous}\n"
                    f"  {project}: {requirement}\n"
                    "Rationalise the constituent project references explicitly rather than "
                    "letting packaging choose a winner."
                )
            merged[package] = requirement
            origin[package] = project

    return dict(sorted(merged.items(), key=lambda kv: kv[0].lower()))


def write_props(path: Path, package_name: str, dependencies: dict[str, tuple[str, str | None, str | None, str | None]]) -> None:
    lines = [
        '<Project>',
        f'  <!-- Generated direct third-party dependencies for {package_name}. Do not edit by hand. -->',
        '  <ItemGroup>',
    ]

    for package, (version, include, exclude, private) in dependencies.items():
        attrs = [
            f"Include={quoteattr(package)}",
            f"Version={quoteattr(version)}",
        ]
        if include:
            attrs.append(f"IncludeAssets={quoteattr(include)}")
        if exclude:
            attrs.append(f"ExcludeAssets={quoteattr(exclude)}")
        if private:
            attrs.append(f"PrivateAssets={quoteattr(private)}")
        lines.append("    <PackageReference " + " ".join(attrs) + " />")

    lines += [
        '  </ItemGroup>',
        '</Project>',
        '',
    ]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    if len(sys.argv) > 2:
        print(f"usage: {Path(sys.argv[0]).name} [repo-root]", file=sys.stderr)
        return 2

    root = Path(sys.argv[1] if len(sys.argv) == 2 else Path(__file__).resolve().parents[1]).resolve()
    generated = root / "dotnet" / "packaging" / "generated"

    for package_name, projects in GROUPS.items():
        deps = merge_dependencies(root, projects)
        out = generated / f"{package_name}.Dependencies.props"
        write_props(out, package_name, deps)
        print(f"{out}: {len(deps)} direct third-party dependencies")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
