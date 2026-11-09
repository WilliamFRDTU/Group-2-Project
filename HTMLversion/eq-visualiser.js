(() => {
  'use strict';

  const canvas = document.querySelector('#visualizer');
  if (!canvas) return;

  const context = canvas.getContext('2d');
  let analyser = null;
  let animationFrame = null;

  function resize() {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;

    canvas.width = Math.round(rect.width * dpr);
    canvas.height = Math.round(rect.height * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  function draw() {
    const rect = canvas.getBoundingClientRect();
    const width = rect.width;
    const height = rect.height;

    context.clearRect(0, 0, width, height);

    if (analyser) {
      const values = new Uint8Array(analyser.frequencyBinCount);
      analyser.getByteFrequencyData(values);

      const gradient = context.createLinearGradient(0, 0, width, 0);
      gradient.addColorStop(0, 'rgba(43, 71, 201, 0.20)');
      gradient.addColorStop(0.5, 'rgba(36, 128, 79, 0.16)');
      gradient.addColorStop(1, 'rgba(184, 121, 8, 0.20)');

      context.fillStyle = gradient;
      context.beginPath();
      context.moveTo(0, height);

      for (let i = 0; i < values.length; i++) {
        const x = (i / (values.length - 1)) * width;
        const level = values[i] / 255;
        const y = height - level * height * 0.8;
        context.lineTo(x, y);
      }

      context.lineTo(width, height);
      context.closePath();
      context.fill();
    }

    animationFrame = requestAnimationFrame(draw);
  }

  function connect() {
    analyser = window.eqTrainerVisuals?.getAnalyser() || null;
    resize();
    draw();
  }

  window.addEventListener('resize', resize);

  if (document.fonts) {
    document.fonts.ready.then(connect);
  } else {
    connect();
  }
})();