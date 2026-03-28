import os
import threading

from flask import Flask, jsonify, request
import requests
import numpy as np
import tensorflow as tf
import matplotlib.pyplot as plt
from datetime import datetime, timedelta


# ─────────────────────────────────────────
# NEURONA CON ÉPOCAS Y GRÁFICA
# ─────────────────────────────────────────
class NeuronaCambio:
    def __init__(self):
        self.tipo_cambio = None
        self.ultima_actualizacion = None
        self.modelo = None
        self.capa = None

    # ─── Obtener tipo de cambio real (se actualiza 1 vez al día ~2 AM México) ───
    def actualizar_tipo_cambio(self):
        ahora = datetime.now()
        if (self.ultima_actualizacion is None or
            ahora - self.ultima_actualizacion >= timedelta(hours=24)):
            try:
                url = "https://api.exchangerate-api.com/v4/latest/USD"
                respuesta = requests.get(url)
                datos = respuesta.json()
                self.tipo_cambio = datos["rates"]["MXN"]
                self.ultima_actualizacion = ahora
                print(f"   Tipo de cambio ACTUALIZADO desde API")
                print(f"   1 USD = {self.tipo_cambio:.2f} MXN")
                print(f"   Próxima actualización: {(ahora + timedelta(hours=24)).strftime('%d/%m/%Y %H:%M')}")
            except:
                self.tipo_cambio = 17.15
                print(f"   Error al obtener tipo de cambio. Usando: {self.tipo_cambio:.2f} MXN")
        else:
            tiempo_restante = timedelta(hours=24) - (ahora - self.ultima_actualizacion)
            horas = int(tiempo_restante.total_seconds() // 3600)
            minutos = int((tiempo_restante.total_seconds() % 3600) // 60)
            print(f"   Tipo de cambio en memoria: 1 USD = {self.tipo_cambio:.2f} MXN")
            print(f"   Próxima actualización en: {horas}h {minutos}m")

    # ─── Entrenar neurona con el tipo de cambio real ───
    def entrenar(self, epochs=1000):
        self.actualizar_tipo_cambio()

        # Datos de entrenamiento usando el tipo de cambio REAL de la API
        dolares = np.array([1, 5, 10, 20, 50, 100, 200, 500], dtype=float)
        pesos   = dolares * self.tipo_cambio

        self.capa = tf.keras.layers.Dense(units=1, input_shape=[1])
        self.modelo = tf.keras.Sequential([self.capa])
        self.modelo.compile(
            optimizer=tf.keras.optimizers.Adam(0.1),
            loss='mean_squared_error'
        )

        print(f"\n   Entrenando neurona con {epochs} épocas...")
        historial = self.modelo.fit(dolares, pesos, epochs=epochs, verbose=False)
        print(f"   Entrenamiento completado!")
        print(f"   Pesos aprendidos : {[round(float(w), 4) for w in self.capa.get_weights()[0]]}")
        print(f"   Bias aprendido   : {round(float(self.capa.get_weights()[1][0]), 4)}")
        print(f"   (El peso debe acercarse a {self.tipo_cambio:.2f} y el bias a 0.0)")

        return historial

    # ─── Procesar pago y devolver cambio ───
    def procesar_pago(self, precio_mxn, pago, moneda_pago):
        print(f"\n   Precio del producto : ${precio_mxn:.2f} MXN")

        if moneda_pago.upper() == "USD":
            pago_en_pesos = self.modelo.predict(np.array([pago]), verbose=0)[0][0]
            real          = pago * self.tipo_cambio
            print(f"   Pago               : ${pago:.2f} USD")
            print(f"   Neurona convierte  : ${pago_en_pesos:.2f} MXN")
            print(f"   Valor real API     : ${real:.2f} MXN")
        else:
            pago_en_pesos = pago
            print(f"   Pago recibido      : ${pago_en_pesos:.2f} MXN")

        if pago_en_pesos < precio_mxn:
            print(f"   Pago insuficiente. Faltan ${precio_mxn - pago_en_pesos:.2f} MXN")
            return None

        cambio = pago_en_pesos - precio_mxn
        return cambio


app = Flask(__name__)
neurona = NeuronaCambio()
neurona_lock = threading.Lock()


def _redondear_mxn(valor):
    return round(float(valor), 2)


def _redondear_tipo_cambio(valor):
    return round(float(valor), 6)


def _extraer_escalar(prediccion):
    # reshape(-1)[0] evita warnings de NumPy 1.25+ al convertir arrays a escalar.
    arreglo = np.asarray(prediccion, dtype=float)
    return float(arreglo.reshape(-1)[0])


def _requiere_reentrenamiento():
    return (
        neurona.modelo is None or
        neurona.tipo_cambio is None or
        neurona.ultima_actualizacion is None or
        datetime.now() - neurona.ultima_actualizacion >= timedelta(hours=24)
    )


def asegurar_neurona_lista():
    with neurona_lock:
        if _requiere_reentrenamiento():
            neurona.entrenar(epochs=1000)


@app.get("/health")
def health_check():
    asegurar_neurona_lista()
    return jsonify({
        "status": "ok",
        "tipo_cambio": _redondear_tipo_cambio(neurona.tipo_cambio),
        "ultima_actualizacion": neurona.ultima_actualizacion.isoformat() if neurona.ultima_actualizacion else None,
    })


@app.post("/calcular")
def calcular():
    payload = request.get_json(silent=True) or {}

    try:
        precio_mxn = float(payload["precio_mxn"])
        pago_usd = float(payload["pago_usd"])
    except (KeyError, TypeError, ValueError):
        return jsonify({
            "message": "Se requieren precio_mxn y pago_usd como números válidos."
        }), 400

    if precio_mxn <= 0:
        return jsonify({"message": "precio_mxn debe ser mayor a 0."}), 400

    if pago_usd <= 0:
        return jsonify({"message": "pago_usd debe ser mayor a 0."}), 400

    try:
        asegurar_neurona_lista()

        with neurona_lock:
            prediccion = neurona.modelo.predict(np.array([[pago_usd]], dtype=float), verbose=0)
            pago_convertido = _extraer_escalar(prediccion)
            tipo_cambio = float(neurona.tipo_cambio)

        valor_real_api = pago_usd * tipo_cambio
        cambio_mxn = pago_convertido - precio_mxn

        return jsonify({
            "cambio_mxn": _redondear_mxn(cambio_mxn),
            "pago_convertido": _redondear_mxn(pago_convertido),
            "valor_real_api_mxn": _redondear_mxn(valor_real_api),
            "tipo_cambio": _redondear_tipo_cambio(tipo_cambio),
        })
    except Exception as ex:
        return jsonify({
            "message": "Error al calcular el cambio con la neurona.",
            "detail": str(ex),
        }), 500


asegurar_neurona_lista()


if __name__ == "__main__":
    host = os.environ.get("NEURONA_INTERNAL_HOST", "127.0.0.1")
    port = int(os.environ.get("NEURONA_INTERNAL_PORT", "5001"))
    app.run(host=host, port=port)