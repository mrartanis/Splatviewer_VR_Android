// Run from repository root. Uses the pinned upstream checkout in ../spark_reference.
// Executes the upstream encoder itself to produce independent golden vectors.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const root = process.cwd();
const THREE = await import(pathToFileURL(path.join(root, 'server/vrphoto/static/three.module.js')));
const passSource = fs.readFileSync('server/vrphoto/static/three-pass.js','utf8')
  .replace("from 'three'", `from '${pathToFileURL(path.join(root,'server/vrphoto/static/three.module.js'))}'`);
const PASS = await import('data:text/javascript;base64,'+Buffer.from(passSource).toString('base64'));
const source = fs.readFileSync(path.join(root, '../spark_reference/dist/spark.cjs.js'), 'utf8');
const upstream = {};
new Function('exports', 'require', 'module', source)(upstream, name => {
  if (name === 'three') return THREE;
  if (name === 'three/addons/postprocessing/Pass.js') return PASS;
  throw new Error(`Unexpected upstream dependency: ${name}`);
}, { exports: upstream });
let seed = 12345;
const random = () => ((seed = (Math.imul(seed,1664525)+1013904223) >>> 0) / 4294967296);
const cases = [];
for (let i = 0; i < 64; i++) {
  const center = Array.from({length:3},()=>Math.fround(random()*8-4));
  const logScale = Array.from({length:3},()=>Math.fround(random()*8-10));
  const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(random()*3,random()*3,random()*3));
  const quaternion = q.toArray().map(Math.fround);
  const rgba = Array.from({length:4},()=>Math.fround(random()));
  const packed = new Uint32Array(4);
  upstream.setPackedSplat(packed,0,...center,...logScale.map(Math.exp),...quaternion,rgba[3],...rgba.slice(0,3));
  cases.push({center,logScale,quaternion,rgba,packed:Array.from(packed)});
}
fs.writeFileSync('third_party/spark/encoding-vectors.json',JSON.stringify({cases},null,2)+'\n');
console.log(`Generated ${cases.length} vectors with upstream Spark setPackedSplat`);
