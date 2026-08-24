import test from "node:test";
import assert from "node:assert/strict";
import {
  cloudSignature,
  clusterChangeLocations,
  describeCluster,
  DEFAULT_CLOUD_MARGIN_MM,
  type ChangeLocation,
} from "./revisionClouds.js";

/**
 * The rule REV-172's acceptance criteria are actually about (REV-172):
 * nearby changes fold into one cloud, distant ones do not, the radius is a
 * real knob, and the same diff run twice produces the same cluster identities
 * so the Revit side can tell "already drawn" from "new".
 */

function change(overrides: Partial<ChangeLocation> & { uniqueId: string }): ChangeLocation {
  return {
    elementId: 1,
    level: "3 этаж",
    label: "Стены «Кладка 250»",
    x: 0,
    y: 0,
    ...overrides,
  };
}

test("8 changes within radius of each other become one cloud, not eight", () => {
  const changes = Array.from({ length: 8 }, (_, i) => change({ uniqueId: `u${i}`, elementId: i, x: i * 200, y: 0 }));

  const clusters = clusterChangeLocations(changes);

  assert.equal(clusters.length, 1);
  assert.equal(clusters[0].changeCount, 8);
});

test("two groups far apart on the same level become two clouds", () => {
  const near = [change({ uniqueId: "a1", x: 0, y: 0 }), change({ uniqueId: "a2", x: 500, y: 0 })];
  const far = [change({ uniqueId: "b1", x: 50000, y: 0 }), change({ uniqueId: "b2", x: 50500, y: 0 })];

  const clusters = clusterChangeLocations([...near, ...far]);

  assert.equal(clusters.length, 2);
  assert.deepEqual(
    clusters.map((c) => c.changeCount).sort(),
    [2, 2]
  );
});

test("a chain of near neighbours joins into one cloud even though the ends are far apart", () => {
  // 0 -- 2000 -- 4000 -- 6000: each hop is within the default radius (3000mm),
  // but point 0 and point 6000 are not. Single-link clustering must still fold
  // all four into one cloud — that is what makes "one big room" work.
  const changes = [
    change({ uniqueId: "a", x: 0, y: 0 }),
    change({ uniqueId: "b", x: 2000, y: 0 }),
    change({ uniqueId: "c", x: 4000, y: 0 }),
    change({ uniqueId: "d", x: 6000, y: 0 }),
  ];

  const clusters = clusterChangeLocations(changes, { radiusMm: 3000 });

  assert.equal(clusters.length, 1);
  assert.equal(clusters[0].changeCount, 4);
});

test("different levels never share a cloud", () => {
  const changes = [
    change({ uniqueId: "a", level: "3 этаж", x: 0, y: 0 }),
    change({ uniqueId: "b", level: "4 этаж", x: 0, y: 0 }), // same x/y, different level
  ];

  const clusters = clusterChangeLocations(changes);

  assert.equal(clusters.length, 2);
  assert.deepEqual(
    clusters.map((c) => c.level).sort(),
    ["3 этаж", "4 этаж"]
  );
});

test("the cluster radius is a real knob", () => {
  const changes = [change({ uniqueId: "a", x: 0, y: 0 }), change({ uniqueId: "b", x: 1000, y: 0 })];

  assert.equal(clusterChangeLocations(changes, { radiusMm: 500 }).length, 2, "tighter radius splits them");
  assert.equal(clusterChangeLocations(changes, { radiusMm: 1500 }).length, 1, "looser radius joins them");
});

test("the cloud rectangle is the cluster's bounds expanded by the margin", () => {
  const changes = [
    change({ uniqueId: "a", x: 0, y: 0 }),
    change({ uniqueId: "b", x: 1000, y: 500 }),
  ];

  const [cluster] = clusterChangeLocations(changes, { marginMm: 200 });

  assert.deepEqual(cluster.boundsMm, { minX: 0, minY: 0, maxX: 1000, maxY: 500 });
  assert.deepEqual(cluster.cloudBoundsMm, { minX: -200, minY: -200, maxX: 1200, maxY: 700 });
});

test("a single point with zero margin still gets a real rectangle, not a zero-width sliver", () => {
  const [cluster] = clusterChangeLocations([change({ uniqueId: "a", x: 1000, y: 1000 })], { marginMm: 0 });

  assert.ok(cluster.cloudBoundsMm.maxX - cluster.cloudBoundsMm.minX >= 200);
  assert.ok(cluster.cloudBoundsMm.maxY - cluster.cloudBoundsMm.minY >= 200);
});

test("the default margin is applied when none is given", () => {
  const changes = [change({ uniqueId: "a", x: 0, y: 0 })];
  const [cluster] = clusterChangeLocations(changes);

  assert.equal(cluster.cloudBoundsMm.minX, -DEFAULT_CLOUD_MARGIN_MM);
});

// --- signature: what makes a re-run recognise its own work -------------------

test("cloudSignature does not depend on the order uniqueIds arrive in", () => {
  assert.equal(cloudSignature(["a", "b", "c"]), cloudSignature(["c", "a", "b"]));
});

test("cloudSignature changes when membership changes", () => {
  assert.notEqual(cloudSignature(["a", "b", "c"]), cloudSignature(["a", "b", "d"]));
  assert.notEqual(cloudSignature(["a", "b"]), cloudSignature(["a", "b", "c"]));
});

test("re-clustering the identical diff produces identical signatures", () => {
  const changes = Array.from({ length: 8 }, (_, i) => change({ uniqueId: `u${i}`, elementId: i, x: i * 200, y: 0 }));

  const first = clusterChangeLocations(changes)[0].signature;
  // Same changes, different arrival order — as a second `compare_model_versions`
  // run over the same underlying diff might hand them over.
  const second = clusterChangeLocations([...changes].reverse())[0].signature;

  assert.equal(first, second);
});

test("labels beyond the cap are marked truncated rather than silently dropped", () => {
  const changes = Array.from({ length: 9 }, (_, i) =>
    change({ uniqueId: `u${i}`, elementId: i, x: i * 100, y: 0, label: `элемент ${i}` })
  );

  const [cluster] = clusterChangeLocations(changes);

  assert.ok(cluster.labels.length < cluster.changeCount);
  assert.equal(cluster.labelsTruncated, true);
});

test("describeCluster folds the overflow into 'и ещё N' instead of listing nine labels", () => {
  const changes = Array.from({ length: 9 }, (_, i) =>
    change({ uniqueId: `u${i}`, elementId: i, x: i * 100, y: 0, label: `элемент ${i}` })
  );

  const text = describeCluster(clusterChangeLocations(changes)[0]);

  assert.match(text, /^Изменений: 9 — /);
  assert.match(text, /и ещё 4\.$/);
});
