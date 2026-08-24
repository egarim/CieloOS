import * as THREE from 'three';

export async function initHero(canvas: HTMLCanvasElement): Promise<(() => void) | null> {
  let renderer: THREE.WebGLRenderer;
  try {
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' });
  } catch (error) {
    // Falling back is correct; falling back SILENTLY is not. Without this line a
    // machine that quietly loses WebGL looks identical to one that never had it,
    // and the still composition is indistinguishable from the animated one being
    // broken — which is exactly how this was found.
    console.warn('CieloOS: the WebGL hero could not start; showing the still composition.', error);
    return null;
  }

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(48, 1, 0.1, 40);
  camera.position.set(0, 0.2, 9);
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.6));
  renderer.outputColorSpace = THREE.SRGBColorSpace;

  const human = new THREE.Color('#3b82f6');
  const agent = new THREE.Color('#8b5cf6');
  const pale = new THREE.Color('#b9d2ea');
  const group = new THREE.Group();
  scene.add(group);

  const pathMat = (color: THREE.Color, opacity = 0.72) => new THREE.LineBasicMaterial({ color, transparent: true, opacity });
  const curveLine = (points: THREE.Vector3[], color: THREE.Color) => {
    const curve = new THREE.CatmullRomCurve3(points);
    const geo = new THREE.BufferGeometry().setFromPoints(curve.getPoints(90));
    const line = new THREE.Line(geo, pathMat(color)); group.add(line); return curve;
  };

  const left = curveLine([new THREE.Vector3(-5, 2.5, 0), new THREE.Vector3(-2.8, 1.5, .2), new THREE.Vector3(-1, .2, 0), new THREE.Vector3(0, 0, 0)], human);
  const right = curveLine([new THREE.Vector3(5, -2.1, 0), new THREE.Vector3(2.8, -1.2, -.2), new THREE.Vector3(1, -.15, 0), new THREE.Vector3(0, 0, 0)], agent);
  const destinations = [-2.5, -1.25, 0, 1.25, 2.5].map((y, i) => curveLine([new THREE.Vector3(.18, 0, 0), new THREE.Vector3(1.8, y * .18, 0), new THREE.Vector3(4.8, y, -.1)], i % 2 ? agent : pale));

  const gate = new THREE.Mesh(new THREE.TorusGeometry(.46, .075, 12, 64), new THREE.MeshBasicMaterial({ color: '#ffffff', transparent: true, opacity: .95 }));
  gate.rotation.y = .32; group.add(gate);
  const glowMaterial = new THREE.MeshBasicMaterial({ color: '#7db6ff', transparent: true, opacity: .1, blending: THREE.AdditiveBlending, depthWrite: false });
  const glow = new THREE.Mesh(new THREE.CircleGeometry(.9, 64), glowMaterial);
  glow.position.z = -.15; group.add(glow);

  const particles: { mesh: THREE.Mesh; curve: THREE.CatmullRomCurve3; offset: number; speed: number }[] = [];
  const dotGeo = new THREE.SphereGeometry(.045, 8, 8);
  const addDots = (curve: THREE.CatmullRomCurve3, color: THREE.Color, count: number) => {
    for (let i = 0; i < count; i++) {
      const mesh = new THREE.Mesh(dotGeo, new THREE.MeshBasicMaterial({ color, transparent: true, opacity: .78 }));
      group.add(mesh); particles.push({ mesh, curve, offset: i / count, speed: .045 + Math.random() * .025 });
    }
  };
  addDots(left, human, 20); addDots(right, agent, 20); destinations.forEach((c, i) => addDots(c, i % 2 ? agent : pale, 8));

  let mx = 0, my = 0, raf = 0, running = true;
  const pointer = (e: PointerEvent) => { mx = (e.clientX / innerWidth - .5) * 2; my = (e.clientY / innerHeight - .5) * 2; };
  window.addEventListener('pointermove', pointer, { passive: true });
  const resize = () => {
    const rect = canvas.getBoundingClientRect();
    renderer.setSize(rect.width, rect.height, false); camera.aspect = rect.width / rect.height; camera.updateProjectionMatrix();
  };
  resize(); window.addEventListener('resize', resize);
  const clock = new THREE.Clock();
  const render = () => {
    if (!running) return;
    const t = clock.getElapsedTime();
    group.rotation.y += (mx * .055 - group.rotation.y) * .035;
    group.rotation.x += (-my * .035 - group.rotation.x) * .035;
    particles.forEach(p => p.mesh.position.copy(p.curve.getPoint((p.offset + t * p.speed) % 1)));
    gate.scale.setScalar(1 + Math.sin(t * 1.8) * .035); glowMaterial.opacity = .08 + Math.sin(t * 1.4) * .025;
    renderer.render(scene, camera); raf = requestAnimationFrame(render);
  };
  render();
  return () => { running = false; cancelAnimationFrame(raf); window.removeEventListener('pointermove', pointer); window.removeEventListener('resize', resize); renderer.dispose(); };
}
