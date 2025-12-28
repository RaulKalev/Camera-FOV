using System;
using System.Globalization;
using System.Windows;
using Autodesk.Revit.DB;

namespace Camera_FOV.Services
{
    public static class FOVCalculator
    {
        public static XYZ RotateVector(XYZ vector, double angleDegrees, XYZ axis = null)
        {
            if (axis == null)
                axis = new XYZ(0, 0, 1);

            double angleRadians = angleDegrees * Math.PI / 180.0;
            double cosAngle = Math.Cos(angleRadians);
            double sinAngle = Math.Sin(angleRadians);

            double x = vector.X, y = vector.Y, z = vector.Z;
            double ux = axis.X, uy = axis.Y, uz = axis.Z;

            return new XYZ(
                cosAngle * x + (1 - cosAngle) * ux * ux * x + sinAngle * uy * z - sinAngle * uz * y,
                cosAngle * y + (1 - cosAngle) * uy * uy * y + sinAngle * uz * x - sinAngle * ux * z,
                cosAngle * z + (1 - cosAngle) * uz * uz * z + sinAngle * ux * y - sinAngle * uy * x
            );
        }

        public static (XYZ, XYZ) CalculateFOVEndpoints(XYZ cameraPosition, double fovAngle, double maxDistanceMM, double rotationAngle)
        {
            // Removed feet conversion
            double distanceMM = maxDistanceMM;
            double halfFov = Math.PI * (fovAngle / 2) / 180.0;

            XYZ baseLeftDirection = new XYZ(-Math.Sin(halfFov), -Math.Cos(halfFov), 0);
            XYZ baseRightDirection = new XYZ(Math.Sin(halfFov), -Math.Cos(halfFov), 0);

            XYZ leftDirection = RotateVector(baseLeftDirection, rotationAngle);
            XYZ rightDirection = RotateVector(baseRightDirection, rotationAngle);

            XYZ leftEnd = cameraPosition + leftDirection.Multiply(distanceMM);
            XYZ rightEnd = cameraPosition + rightDirection.Multiply(distanceMM);

            return (leftEnd, rightEnd);
        }

    }
}
