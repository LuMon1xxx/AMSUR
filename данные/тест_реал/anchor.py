# -*- coding: utf-8 -*-
from teachers_grid import TEACH
from classes_grid import CLS

for cls in ["6а", "6б", "6в", "6г", "7а"]:
    have = []
    for t, td in TEACH.items():
        for slot in (5, 6, 7, 8):
            m = td.get("Чт", {}).get(slot)
            if m and (cls in m):
                have.append(t + "@" + str(slot) + "=" + m)
    print(cls + " Thu5-8: " + "; ".join(have))
for cls in ["6А", "6Б", "6В", "6Г"]:
    print(cls + " Thu: " + str(CLS[cls]["Чт"]))
