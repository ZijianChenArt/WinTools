import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { MTLLoader } from 'three/addons/loaders/MTLLoader.js';
import { FBXLoader } from 'three/addons/loaders/FBXLoader.js';

const params = new URLSearchParams(location.search);
const session = params.get('session');
const file = params.get('file');
const source = new URL(encodeURIComponent(file), 'https://wintools-model-files.local/');
source.searchParams.set('__preview', session);
const hint = document.getElementById('hint');
const dark = params.get('dark') === '1';
document.body.classList.toggle('dark', dark);
let renderer, scene, camera, controls, model, observer, disposed = false, scheduled = false;
const abort = new AbortController(), blobUrls = new Set();
const report = (type, detail = {}) => window.chrome?.webview?.postMessage({ type, session, ...detail });
function requestRender() {
  if (scheduled || disposed || !renderer) return;
  scheduled = true;
  requestAnimationFrame(() => { scheduled = false; if (!disposed) renderer.render(scene, camera); });
}
const localUrl = (path) => {
  const url = new URL(path.replaceAll('\\', '/'), source);
  if (url.protocol === 'blob:') blobUrls.add(url.href);
  if (!['blob:', 'data:'].includes(url.protocol) && url.origin !== source.origin)
    return 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==';
  if (url.origin === source.origin) url.searchParams.set('__preview', session);
  return url.href;
};
const manager = new THREE.LoadingManager();
manager.setURLModifier(localUrl);
manager.onLoad = () => {
  // A failed local texture leaves a Texture with no image. Keep the material's diffuse colour.
  model?.traverse(object => {
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      if (!material) continue;
      for (const [key, value] of Object.entries(material)) {
        if (value?.isTexture && !value.image) { value.dispose(); material[key] = null; material.needsUpdate = true; }
      }
    }
  });
  requestRender();
};
manager.onError = () => requestRender(); // Missing textures must not hide otherwise readable geometry.

async function load() {
  const started = performance.now();
  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'low-power' });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.setClearColor(dark ? 0x202020 : 0xf5f5f5);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  document.body.prepend(renderer.domElement);
  scene = new THREE.Scene();
  camera = new THREE.PerspectiveCamera(40, 1, 0.01, 100);
  scene.add(new THREE.HemisphereLight(0xffffff, 0x5d6675, 2.5));
  const key = new THREE.DirectionalLight(0xffffff, 3); key.position.set(3, 5, 4); scene.add(key);
  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = false;
  controls.addEventListener('change', requestRender);
  const data = await fetch(source, { signal: abort.signal, cache: 'no-store' }).then(response => { if (!response.ok) throw new Error('无法读取模型'); return response.arrayBuffer(); });
  if (disposed) return;
  if (file.toLowerCase().endsWith('.fbx')) {
    model = new FBXLoader(manager).parse(data, source.href.slice(0, source.href.lastIndexOf('/') + 1));
  } else {
    const text = new TextDecoder().decode(data);
    const loader = new OBJLoader(manager);
    // Use a sidecar material when present, but always allow geometry without a material file.
    const material = /^\s*mtllib\s+(.+?)\s*$/m.exec(text)?.[1];
    if (material) {
      try {
        const mtlUrl = localUrl(material);
        const response = await fetch(mtlUrl, { signal: AbortSignal.timeout(1000), cache: 'no-store' });
        if (response.ok) loader.setMaterials(new MTLLoader(manager).parse(await response.text(), new URL('.', mtlUrl).href));
      } catch { /* Geometry remains useful if a sidecar is missing or blocked. */ }
    }
    if (disposed) return;
    model = loader.parse(text);
  }
  if (disposed) return;
  const bounds = new THREE.Box3().setFromObject(model);
  if (bounds.isEmpty()) throw new Error('模型没有可显示的几何体');
  const size = bounds.getSize(new THREE.Vector3());
  const longest = Math.max(size.x, size.y, size.z);
  if (!Number.isFinite(longest) || longest <= 0) throw new Error('模型尺寸无效');
  const pivot = new THREE.Group();
  model.position.sub(bounds.getCenter(new THREE.Vector3()));
  pivot.add(model); pivot.scale.setScalar(2 / longest); scene.add(pivot);
  let triangles = 0, meshes = 0;
  model.traverse(item => {
    if (!item.isMesh) return;
    meshes++;
    triangles += Math.floor((item.geometry.index?.count ?? item.geometry.attributes.position?.count ?? 0) / 3);
    if (!item.geometry.attributes.normal) item.geometry.computeVertexNormals();
  });
  if (!meshes) throw new Error('模型没有可显示的网格');
  camera.position.set(3, 2.2, 3); controls.target.set(0, 0, 0); controls.update();
  let previousFitDistance = 0;
  function resize() {
    camera.aspect = innerWidth / Math.max(1, innerHeight); camera.updateProjectionMatrix();
    const limitingFov = Math.min(THREE.MathUtils.degToRad(camera.fov), 2 * Math.atan(Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.aspect));
    const distance = Math.sqrt(3) / Math.sin(limitingFov / 2) * 1.1;
    if (!previousFitDistance || distance > previousFitDistance) {
      const direction = camera.position.clone().sub(controls.target).normalize();
      camera.position.copy(controls.target).addScaledVector(direction, Math.max(camera.position.distanceTo(controls.target), distance));
    }
    previousFitDistance = distance;
    camera.far = Math.max(100, distance * 20); camera.updateProjectionMatrix();
    renderer.setSize(innerWidth, innerHeight, false); requestRender();
  }
  observer = new ResizeObserver(resize); observer.observe(document.body); resize();
  renderer.render(scene, camera);
  hint.remove();
  window.__modelPreview = { meshes, triangles, loadMs: performance.now() - started, ready: true,
    get frames() { return renderer.info.render.frame; } };
  report('model-ready', window.__modelPreview);
}
function dispose() {
  if (disposed) return;
  disposed = true; abort.abort(); observer?.disconnect(); controls?.dispose();
  const geometries = new Set(), materials = new Set(), textures = new Set();
  scene?.traverse(object => {
    if (object.geometry) geometries.add(object.geometry);
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      if (!material) continue;
      materials.add(material);
      for (const value of Object.values(material)) if (value?.isTexture) textures.add(value);
    }
  });
  textures.forEach(texture => texture.dispose()); materials.forEach(material => material.dispose()); geometries.forEach(geometry => geometry.dispose());
  blobUrls.forEach(url => URL.revokeObjectURL(url)); blobUrls.clear();
  renderer?.dispose(); renderer?.forceContextLoss();
}
addEventListener('pagehide', dispose, { once: true });
load().catch(() => { if (!disposed) { hint.textContent = '无法显示模型，请用默认程序打开。'; report('model-error'); dispose(); } });
