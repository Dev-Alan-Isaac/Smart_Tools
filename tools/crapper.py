"""CRAP score per method from a Cobertura coverage file.

    CRAP = complexity^2 * (1 - coverage)^3 + complexity

Usage: python -I crapper.py <coverage.cobertura.xml> [limit=10]
Prints every method at or above the limit, worst first. Exit code 1 when there are any, so a script can gate on it.
Only code the tests load is in the file: a method nobody runs is not listed, so read this with the coverage figure.
"""
import sys
import xml.etree.ElementTree as ET


def crap(complexity: int, line_rate: float) -> float:
    return complexity ** 2 * (1 - line_rate) ** 3 + complexity


def main() -> int:
    path = sys.argv[1]
    limit = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
    rows = []
    for cls in ET.parse(path).getroot().iter("class"):
        for m in cls.iter("method"):
            c = int(m.get("complexity", "1"))
            cov = float(m.get("line-rate", "0"))
            rows.append((crap(c, cov), c, cov, cls.get("name"), m.get("name")))
    rows.sort(reverse=True)
    over = [r for r in rows if r[0] >= limit]
    print(f"{len(rows)} methods, {len(over)} with CRAP >= {limit:g}")
    for score, c, cov, cls, name in over:
        print(f"  CRAP {score:7.1f}  complexity {c:3d}  coverage {cov:5.0%}  {cls}.{name}")
    return 1 if over else 0


if __name__ == "__main__":
    sys.exit(main())
