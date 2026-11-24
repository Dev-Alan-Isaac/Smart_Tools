"""Finds the same method body copied into more than one place.

Usage: python -I dryer.py <folder> [min_lines=5] [how_many=40]

Bodies are compared after comments and blank lines are removed, so a copy that only differs in
whitespace or comments still counts. It lists each group with the lines you would save by sharing
one copy ("wasted lines"), biggest first. It finds exact copies only: a near copy with one changed
line is not reported, so a clean report is not proof there is no duplication.
"""
import collections
import hashlib
import os
import re
import sys

SIG = re.compile(r'^\s*(?:(?:public|private|internal|protected|static|async|override|sealed|partial|unsafe)\s+)*'
                 r'[\w<>\[\],\.\?\(\) ]+?\s+(\w+)\s*\([^;{}]*\)\s*(?:where [^{]+)?$')
NOT_A_METHOD = ('if', 'for', 'while', 'switch', 'foreach', 'else', 'using', 'lock', 'catch', 'return', 'new')
SKIP_DIRS = {'obj', 'bin', 'Engines', '.git', 'publish'}


def method_bodies(path):
    try:
        lines = open(path, encoding='utf-8-sig').read().replace('\r\n', '\n').split('\n')
    except OSError:
        return
    i = 0
    while i < len(lines):
        m = SIG.match(lines[i])
        starts = lines[i].strip().startswith(NOT_A_METHOD)
        if m and not starts and i + 1 < len(lines) and lines[i + 1].strip() == '{':
            depth, j, body = 0, i + 1, []
            while j < len(lines):
                depth += lines[j].count('{') - lines[j].count('}')
                body.append(lines[j])
                if depth == 0:
                    break
                j += 1
            yield i + 1, m.group(1), body
            i = j
        i += 1


def main() -> None:
    root = os.path.abspath(sys.argv[1])
    min_lines = int(sys.argv[2]) if len(sys.argv) > 2 else 5
    top = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    groups = collections.defaultdict(list)
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            if not name.endswith('.cs') or name.endswith(('.g.cs', '.g.i.cs')):
                continue
            path = os.path.join(dirpath, name)
            for line, method, body in method_bodies(path):
                if len(body) < min_lines:
                    continue
                norm = '\n'.join(re.sub(r'//.*', '', b).strip() for b in body if b.strip() and not b.strip().startswith('//'))
                groups[hashlib.md5(norm.encode()).hexdigest()].append((os.path.relpath(path, root), line, method, len(body)))
    found = sorted(((sum(x[3] for x in g[1:]), g) for g in groups.values() if len(g) > 1), key=lambda t: -t[0])
    print(f"{len(found)} groups of copied methods, {sum(w for w, _ in found)} wasted lines")
    for wasted, group in found[:top]:
        print(f"--- {wasted} wasted lines, {len(group)} copies")
        for path, line, method, length in group:
            print(f"    {path}:{line} {method} ({length} lines)")


if __name__ == '__main__':
    main()
