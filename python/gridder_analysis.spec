# -*- mode: python ; coding: utf-8 -*-
"""
PyInstaller spec for gridder_analysis standalone executable.

Build with:  pyinstaller gridder_analysis.spec
Output:      dist/gridder_analysis/gridder_analysis.exe
"""

import os
import sys
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

block_cipher = None

# Collect madmom model files (neural network weights needed at runtime)
madmom_datas = collect_data_files('madmom', includes=['**/*.npz', '**/*.pkl', '**/*.npy'])
# Collect madmom submodules (uses dynamic imports internally)
# collect_submodules needs pkg_resources at analysis time; enumerate manually if it fails
try:
    madmom_hiddenimports = collect_submodules('madmom')
except Exception:
    madmom_hiddenimports = [
        'madmom',
        'madmom.audio',
        'madmom.audio.chroma',
        'madmom.audio.comb_filters',
        'madmom.audio.filters',
        'madmom.audio.signal',
        'madmom.audio.spectrogram',
        'madmom.audio.stft',
        'madmom.evaluation',
        'madmom.evaluation.beats',
        'madmom.features',
        'madmom.features.beats',
        'madmom.features.beats_hmm',
        'madmom.features.downbeats',
        'madmom.features.onsets',
        'madmom.features.tempo',
        'madmom.io',
        'madmom.io.audio',
        'madmom.io.midi',
        'madmom.ml',
        'madmom.ml.nn',
        'madmom.ml.nn.activations',
        'madmom.ml.nn.layers',
        'madmom.processors',
        'madmom.utils',
    ]

# Collect librosa data files (e.g. example audio, tone data)
librosa_datas = collect_data_files('librosa')

# Collect soundfile shared libraries
soundfile_datas = collect_data_files('soundfile')

# Also try _soundfile_data which some versions use for the libsndfile binary
try:
    soundfile_datas += collect_data_files('_soundfile_data')
except Exception:
    pass

a = Analysis(
    ['pyinstaller_entry.py'],
    pathex=[],
    binaries=[],
    datas=madmom_datas + librosa_datas + soundfile_datas,
    hiddenimports=madmom_hiddenimports + [
        'gridder_analysis',
        'gridder_analysis.__main__',
        'gridder_analysis.beat_detector',
        'gridder_analysis.tempo_segmenter',
        'gridder_analysis.waveform_generator',
        'librosa',
        'librosa.core',
        'librosa.feature',
        'librosa.onset',
        'librosa.beat',
        'librosa.decompose',
        'librosa.util',
        'soundfile',
        'numpy',
        'scipy',
        'scipy.signal',
        'scipy.fft',
        'scipy.ndimage',
        'scipy.sparse',
        'scipy.special',
        'audioread',
        'soxr',
        'pkg_resources',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        'tkinter',
        'matplotlib',
        'PIL',
        'IPython',
        'jupyter',
        'pytest',
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='gridder_analysis',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,  # CLI app — needs console for stdout/stderr
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='gridder_analysis',
)
