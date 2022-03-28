
from itertools import zip_longest
import csv


def chunk(arr, n):
    return (arr[pos:pos + n] for pos in range(0, len(arr), n))


with open("Output.obj", "w") as obj_file, open("OutputVertices.csv", "r") as vert_file, open("OutputIndices.txt", "r") as idx_file:
    vert_data = csv.reader(vert_file)
    next(vert_data)
    obj_file.write("o Flag\n")
    for vert in vert_data:
        x, y, z, _ = map(float, vert[1:])
        obj_file.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")

    for face in chunk(list(map(int, idx_file.readlines())), 3):
        obj_file.write(f"f {face[0]} {face[1]} {face[2]}\n")
